// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using NuGet.Versioning;
using System;

namespace RepoUtil
{
    internal struct NuGetPackage : IEquatable<NuGetPackage>
    {
        internal string Name { get; }
        internal NuGetVersion Version { get; }

        internal NuGetPackage(string name, NuGetVersion version)
        {
            Name = name;
            Version = version;
        }
        internal NuGetPackage(string name, string version)
        {
            Name = name;
            Version = NuGetVersion.Parse(version);
        }

        public bool IsStable()
        {
            return string.IsNullOrEmpty(Version.Release);
        }
        public static bool operator ==(NuGetPackage left, NuGetPackage right) =>
            Constants.NugetPackageNameComparer.Equals(left.Name, right.Name) &&
            NuGetVersion.Equals(left.Version, right.Version);
        public static bool operator !=(NuGetPackage left, NuGetPackage right) => !(left == right);
        public override bool Equals(object obj) => obj is NuGetPackage && Equals((NuGetPackage)obj);
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
        public override string ToString() => $"{Name}-{Version}";
        public bool Equals(NuGetPackage other) => this == other;
    }
}
