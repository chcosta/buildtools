using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;

namespace Microsoft.DotNet.Build.Tasks
{
    public class GatherAssemblyVersionsFromPackageDrop : Task
    {
        [Required]
        public string [] PackageDrops { get; set; }
        [Output]
        public ITaskItem[] AssemblyNames { get; set; }

        public override bool Execute()
        {
            Regex packageNameRegEx = new Regex(@"([^\\]+)\.(\d+\.\d+\.\d+)-([^\.]+)");
            List<ITaskItem> assemblyNameItems = new List<ITaskItem>();

            foreach (string packageDrop in PackageDrops)
            {
                if (!Directory.Exists(packageDrop))
                {
                    Log.LogWarning("PackageDrop does not exist - '{0}'", packageDrop);
                    continue;
                }
                IEnumerable<ITaskItem> packages = Directory.GetFiles(packageDrop)?.Select(f => new TaskItem(f));


                foreach (ITaskItem package in packages)
                {
                    Match m = packageNameRegEx.Match(package.ItemSpec);
                    if (m.Success)
                    {
                        TaskItem assemblyName = new TaskItem(m.Groups[0].Value);
                        assemblyName.SetMetadata("AssemblyName", m.Groups[1].Value);
                        assemblyName.SetMetadata("AssemblyVersion", m.Groups[2].Value);
                        assemblyName.SetMetadata("PackageVersion", m.Groups[3].Value);
                        assemblyNameItems.Add(assemblyName);
                    }
                }
            }
            AssemblyNames = assemblyNameItems?.OrderBy(an => an.ItemSpec.ToString(), StringComparer.Ordinal)?.ToArray();
            return true;
        }
    }
}
