// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using NuGet.Versioning;

namespace RepoUtil
{
    internal sealed class NuGetPackageChange
    {
        internal string Name { get; }
        internal NuGetVersion OldVersion { get; }
        internal NuGetVersion NewVersion { get; }
        internal NuGetPackage OldPackage => new NuGetPackage(Name, OldVersion);
        internal NuGetPackage NewPackage => new NuGetPackage(Name, NewVersion);

        internal NuGetPackageChange(string name, NuGetVersion oldVersion, NuGetVersion newVersion)
        {
            Name = name;
            OldVersion = oldVersion;
            NewVersion = newVersion;
        }

        public override string ToString() => $"{Name} from {OldVersion.ToString()} to {NewVersion.ToString()}";
    }
}
