﻿// <copyright file="UploadGovernor.cs" company="slskd Team">
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
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using slskd.Users;

    /// <summary>
    ///     Governs upload transfer speed.
    /// </summary>
    public interface IUploadGovernor
    {
        /// <summary>
        ///     Asynchronously obtains a grant of <paramref name="requestedBytes"/> for the requesting <paramref name="username"/>.
        /// </summary>
        /// <remarks>
        ///     This operation completes when any number of bytes can be granted. The amount returned may be smaller than the
        ///     requested amount.
        /// </remarks>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="requestedBytes">The number of requested bytes.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation.</param>
        /// <returns>The operation context, including the number of bytes granted.</returns>
        Task<int> GetBytesAsync(string username, int requestedBytes, CancellationToken cancellationToken);

        /// <summary>
        ///     Returns wasted bytes for redistribution.
        /// </summary>
        /// <param name="username">The username of the user that generated the waste.</param>
        /// <param name="attemptedBytes">The number of bytes that were attempted to be transferred.</param>
        /// <param name="grantedBytes">The number of bytes granted by all governors in the system.</param>
        /// <param name="actualBytes">The actual number of bytes transferred.</param>
        public void ReturnBytes(string username, int attemptedBytes, int grantedBytes, int actualBytes);
    }

    /// <summary>
    ///     Governs upload transfer speed.
    /// </summary>
    public class UploadGovernor : IUploadGovernor
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="UploadGovernor"/> class.
        /// </summary>
        /// <param name="userService">The UserService instance to use.</param>
        /// <param name="optionsMonitor">The OptionsMonitor instance to use.</param>
        public UploadGovernor(
            IUserService userService,
            IOptionsMonitor<Options> optionsMonitor)
        {
            Users = userService;

            OptionsMonitor = optionsMonitor;
            OptionsMonitor.OnChange(Configure);

            Configure(OptionsMonitor.CurrentValue);
        }

        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private string LastOptionsHash { get; set; }
        private int LastGlobalSpeedLimit { get; set; }
        private Dictionary<string, ITokenBucket> TokenBuckets { get; set; } = new Dictionary<string, ITokenBucket>();
        private IUserService Users { get; }

        /// <summary>
        ///     Asynchronously obtains a grant of <paramref name="requestedBytes"/> for the requesting <paramref name="username"/>.
        /// </summary>
        /// <remarks>
        ///     This operation completes when any number of bytes can be granted. The amount returned may be smaller than the
        ///     requested amount.
        /// </remarks>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="requestedBytes">The number of requested bytes.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation.</param>
        /// <returns>The operation context, including the number of bytes granted.</returns>
        public Task<int> GetBytesAsync(string username, int requestedBytes, CancellationToken cancellationToken)
        {
            var group = Users.GetGroup(username);
            var bucket = TokenBuckets.GetValueOrDefault(group ?? string.Empty, TokenBuckets[Application.DefaultGroup]);

            return bucket.GetAsync(requestedBytes, cancellationToken);
        }

        /// <summary>
        ///     Returns wasted bytes for redistribution.
        /// </summary>
        /// <param name="username">The username of the user that generated the waste.</param>
        /// <param name="attemptedBytes">The number of bytes that were attempted to be transferred.</param>
        /// <param name="grantedBytes">The number of bytes granted by all governors in the system.</param>
        /// <param name="actualBytes">The actual number of bytes transferred.</param>
        public void ReturnBytes(string username, int attemptedBytes, int grantedBytes, int actualBytes)
        {
            var waste = Math.Max(0, grantedBytes - actualBytes);

            if (waste == 0)
            {
                return;
            }

            var group = Users.GetGroup(username);
            var bucket = TokenBuckets.GetValueOrDefault(group ?? string.Empty, TokenBuckets[Application.DefaultGroup]);

            // we don't have enough information to tell whether grantedBytes was reduced by the global limiter within
            // Soulseek.NET, so we just return the bytes that we know for sure that were wasted, which is grantedBytes - actualBytes.
            // example: we grant 1000 bytes. Soulseek.NET grants only 500. 250 bytes are written. ideally we would return 750
            // bytes, but instead we return 250. this discrepancy doesn't really matter because Soulseek.NET is the constraint in
            // this scenario and the additional tokens we would return would never be used.
            bucket.Return(waste);
        }

        private void Configure(Options options)
        {
            static long ComputeBucketCapacity(int speed)
                => speed * 1024L / 10;

            static TokenBucket CreateBucket(int speed)
                => new(ComputeBucketCapacity(speed), 100);

            var optionsHash = Compute.Sha1Hash(options.Groups.ToJson());

            if (optionsHash == LastOptionsHash && options.Global.Upload.SpeedLimit == LastGlobalSpeedLimit)
            {
                return;
            }

            // build a new dictionary of token buckets based on the current groups, then
            // swap it in for the existing dictionary.  there's risk of inaccuracy here if
            // groups are deleted or users are moved around, as bytes may be taken from or returned
            // to the wrong bucket.  this is acceptable.  reconfiguring buckets replenishes them,
            // also, so transfers in progress will briefly exceed the intended speeds.
            var tokenBuckets = new Dictionary<string, ITokenBucket>()
            {
                { Application.PrivilegedGroup, CreateBucket(options.Global.Upload.SpeedLimit) },
                { Application.DefaultGroup, CreateBucket(options.Groups.Default.Upload.SpeedLimit) },
                { Application.LeecherGroup, CreateBucket(options.Groups.Leechers.Upload.SpeedLimit) },
            };

            foreach (var group in options.Groups.UserDefined)
            {
                tokenBuckets.Add(group.Key, CreateBucket(group.Value.Upload.SpeedLimit));
            }

            TokenBuckets = tokenBuckets;

            LastGlobalSpeedLimit = options.Global.Upload.SpeedLimit;
            LastOptionsHash = optionsHash;
        }
    }
}