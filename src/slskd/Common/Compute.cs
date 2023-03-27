﻿// <copyright file="Compute.cs" company="slskd Team">
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

namespace slskd
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    ///     Computational functions.
    /// </summary>
    public static class Compute
    {
        public static (int Delay, int Jitter) ExponentialBackoffDelay(int iteration, int maxDelayInMilliseconds = int.MaxValue)
        {
            iteration = Math.Min(100, iteration);

            var computedDelay = Math.Floor((Math.Pow(2, iteration) - 1) / 2) * 1000;
            var clampedDelay = (int)Math.Min(computedDelay, maxDelayInMilliseconds);

            var jitter = new Random().Next(1000);

            return (clampedDelay, jitter);
        }

        public static string Sha1Hash(string str)
        {
            using var sha1 = SHA1.Create();
            return BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes(str))).Replace("-", string.Empty);
        }

        public static string Sha256Hash(string str)
        {
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(str))).Replace("-", string.Empty);
        }
    }
}