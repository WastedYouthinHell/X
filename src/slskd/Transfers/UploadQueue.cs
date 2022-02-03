﻿// <copyright file="UploadQueue.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Transfers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using slskd.Users;
    using Soulseek;

    /// <summary>
    ///     Orchestrates uploads.
    /// </summary>
    public interface IUploadQueue
    {
        /// <summary>
        ///     Enqueues an upload.
        /// </summary>
        /// <param name="transfer">The upload to enqueue.</param>
        void Enqueue(Transfer transfer);

        /// <summary>
        ///     Awaits the start of an upload.
        /// </summary>
        /// <param name="transfer">The upload for which to wait.</param>
        /// <returns>The operation context.</returns>
        Task AwaitStartAsync(Transfer transfer);

        /// <summary>
        ///     Signals the completion of an upload.
        /// </summary>
        /// <param name="transfer">The completed upload.</param>
        void Complete(Transfer transfer);
    }

    /// <summary>
    ///     Orchestrates uploads.
    /// </summary>
    public class UploadQueue : IUploadQueue
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="UploadQueue"/> class.
        /// </summary>
        /// <param name="userService">The UserService instance to use.</param>
        /// <param name="optionsMonitor">The OptionsMonitor instance to use.</param>
        public UploadQueue(
            IUserService userService,
            IOptionsMonitor<Options> optionsMonitor)
        {
            Users = userService;

            OptionsMonitor = optionsMonitor;
            OptionsMonitor.OnChange(Configure);

            Configure(OptionsMonitor.CurrentValue);
        }

        private IUserService Users { get; }
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private string LastOptionsHash { get; set; }
        private SemaphoreSlim SyncRoot { get; } = new SemaphoreSlim(1, 1);
        private int MaxSlots { get; set; } = 0;
        private Dictionary<string, Group> Groups { get; set; } = new Dictionary<string, Group>();
        private ConcurrentDictionary<string, List<Upload>> Uploads { get; } = new ConcurrentDictionary<string, List<Upload>>();

        /// <summary>
        ///     Enqueues an upload.
        /// </summary>
        /// <param name="transfer">The upload to enqueue.</param>
        public void Enqueue(Transfer transfer)
        {
            var group = Users.GetGroup(transfer.Username);
            var upload = new Upload() { Username = transfer.Username, Filename = transfer.Filename };

            SyncRoot.Wait();

            try
            {
                Uploads.AddOrUpdate(
                    key: group,
                    addValue: new List<Upload>(new[] { upload }),
                    updateValueFactory: (key, list) =>
                    {
                        list.Add(upload);
                        return list;
                    });
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        /// <summary>
        ///     Awaits the start of an upload.
        /// </summary>
        /// <param name="transfer">The upload for which to wait.</param>
        /// <returns>The operation context.</returns>
        public Task AwaitStartAsync(Transfer transfer)
        {
            var group = Users.GetGroup(transfer.Username);

            SyncRoot.Wait();

            try
            {
                if (!Uploads.TryGetValue(group, out var list))
                {
                    throw new SlskdException($"Error resolving list for upload group '{group}'");
                }

                var entry = list.FirstOrDefault(e => e.Username == transfer.Username && e.Filename == transfer.Filename);

                if (entry == default)
                {
                    throw new SlskdException($"No such transfer: {transfer.Filename} to {transfer.Username}");
                }

                entry.Ready = DateTime.UtcNow;
                return entry.TaskCompletionSource.Task;
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        /// <summary>
        ///     Signals the completion of an upload.
        /// </summary>
        /// <param name="transfer">The completed upload.</param>
        public void Complete(Transfer transfer)
        {
            var group = Users.GetGroup(transfer.Username);

            SyncRoot.Wait();

            try
            {
                Groups[group].UsedSlots = Math.Min(0, Groups[group].UsedSlots - 1);
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        private void Process()
        {
            SyncRoot.Wait();

            try
            {
                if (Groups.Values.Sum(g => g.UsedSlots) >= MaxSlots)
                {
                    return;
                }

                if (!Uploads.Values.Any(v => v.Any(u => u.Ready.HasValue)))
                {
                    return;
                }

                foreach (var group in Groups.Values.OrderBy(g => g.Priority))
                {
                    Console.WriteLine($"Processing group {group.Name} (slots: {group.Slots}, used: {group.UsedSlots}, strategy: {group.Strategy})");

                    if (group.UsedSlots >= group.Slots)
                    {
                        Console.WriteLine($"{group.Name} has no available slots, skipping (used: {group.UsedSlots}, max: {group.Slots})");
                        continue;
                    }

                    if (!Uploads.TryGetValue(group.Name, out var uploads) || !uploads.Any(u => u.Ready.HasValue))
                    {
                        Console.WriteLine($"{group.Name} has no ready uploads, skippling");
                        continue;
                    }

                    var ready = uploads.Where(u => u.Ready.HasValue);
                    Console.WriteLine($"{group.Name} has {uploads.Count} uploads, {ready.Count()} of which are ready");

                    var upload = ready
                        .OrderBy(u => group.Strategy == QueueStrategy.FirstInFirstOut ? u.Enqueued : u.Ready)
                        .FirstOrDefault();

                    Console.WriteLine($"Next upload for group {group.Name} using strategy {group.Strategy}: {upload.Filename} to {upload.Username}");

                    uploads.Remove(upload);
                    upload.TaskCompletionSource.SetResult();
                    group.UsedSlots++;
                }
            }
            finally
            {
                SyncRoot.Release();
            }
        }

        private void Configure(Options options)
        {
            int GetExistingUsedSlotsOrDefault(string group)
                => Groups.ContainsKey(group) ? Groups[group].UsedSlots : 0;

            SyncRoot.Wait();

            try
            {
                MaxSlots = options.Global.Upload.Slots;

                // don't rebuild everything if nothing changed
                if (options.Groups.ToJson().ToSHA1() == LastOptionsHash)
                {
                    return;
                }

                // statically add built-in groups
                var groups = new List<Group>()
                {
                    new Group()
                    {
                        Name = Application.PriviledgedGroup,
                        Priority = 0,
                        Slots = MaxSlots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.PriviledgedGroup),
                        Strategy = QueueStrategy.RoundRobin,
                    },
                    new Group()
                    {
                        Name = Application.DefaultGroup,
                        Priority = options.Groups.Default.Upload.Priority,
                        Slots = options.Groups.Default.Upload.Slots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.DefaultGroup),
                        Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), options.Groups.Default.Upload.Strategy, true),
                    },
                    new Group()
                    {
                        Name = Application.LeecherGroup,
                        Priority = options.Groups.Leechers.Upload.Priority,
                        Slots = options.Groups.Leechers.Upload.Slots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.LeecherGroup),
                        Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), options.Groups.Leechers.Upload.Strategy, true),
                    },
                };

                // dynamically add user-defined groups
                groups.AddRange(options.Groups.UserDefined.Select(kvp => new Group()
                {
                    Name = kvp.Key,
                    Priority = kvp.Value.Upload.Priority,
                    Slots = kvp.Value.Upload.Slots,
                    UsedSlots = GetExistingUsedSlotsOrDefault(kvp.Key),
                    Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), kvp.Value.Upload.Strategy, true),
                }));

                Groups = groups.ToDictionary(g => g.Name);

                LastOptionsHash = options.Groups.ToJson().ToSHA1();
            }
            finally
            {
                SyncRoot.Release();
            }
        }

        private sealed class Group
        {
            public string Name { get; set; }
            public int Slots { get; set; }
            public int Priority { get; set; }
            public QueueStrategy Strategy { get; set; }
            public int UsedSlots { get; set; }
        }

        private sealed class Upload
        {
            public string Username { get; set; }
            public string Filename { get; set; }
            public DateTime Enqueued { get; set; } = DateTime.UtcNow;
            public DateTime? Ready { get; set; } = null;
            public TaskCompletionSource TaskCompletionSource { get; set; } = new TaskCompletionSource();
        }
    }
}
