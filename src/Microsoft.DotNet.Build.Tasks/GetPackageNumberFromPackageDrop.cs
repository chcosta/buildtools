// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks
{
    public sealed class GetPackageNumberFromPackageDrop : Task
    {
        [Required]
        public string[] PackageDrops { get; set; }

        [Output]
        public string PackageNumber { get; set; }

        public override bool Execute()
        {
            foreach (string packageDrop in PackageDrops)
            {
                if (!Directory.Exists(packageDrop))
                {
                    continue;
                }
                string[] files = Directory.GetFiles(packageDrop);
                if (files == null || files.Count() == 0)
                {
                    continue;
                }

                Regex packageMatch = new Regex(@"[^-]+-((\w+-)?\d\d\d\d\d(-\d\d)?)");
                foreach (string file in files)
                {
                    Match m = packageMatch.Match(file);
                    if (m.Success)
                    {
                        PackageNumber = m.Groups[1].Value;
                        return true;
                    }
                }
            }
            return true;
        }
    }
}