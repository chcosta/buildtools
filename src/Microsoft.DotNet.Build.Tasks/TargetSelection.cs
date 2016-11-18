using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.Build.Tasks
{
    public class TargetSelection : Task
    {
        [Required]
        public string SupportedGroups { get; set; }

        [Required]
        public string SelectionGroup { get; set; }

        [Required]
        public ITaskItem[] TargetGroups { get; set; }

        [Required]
        public ITaskItem[] OSGroups { get; set; }

        [Required]
        public string Name { get; set; }

        public bool UseCompileFramework { get; set; }

        public bool AllowCompatibleFramework { get; set; }

        [Output]
        public ITaskItem OutputItem { get; set; }


        public override bool Execute()
        {
            string[] verticalGroups = SupportedGroups.Split(';');
            string[] supportedTargetGroups = verticalGroups.Select(v => v.Split('_')[0]).Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();

            string[] tokens = SelectionGroup.Split('_');
            string osGroup = tokens[0];
            string targetGroup = tokens[1];
            string originalTargetGroup = targetGroup;

            Dictionary<string, string> metadata = new Dictionary<string, string>();

            if (targetGroup == null)
            {
                metadata.Add("TargetGroup", "");
            }
            else
            {
                Dictionary<string, ITaskItem> searchTargetGroups = TargetGroups.ToDictionary(f => f.ItemSpec, f => f);

                // Depth search for a valid framework ie. netcoreapp1.1 => netcoreapp1.0
                targetGroup = FindCompatibleTargetGroup(targetGroup, supportedTargetGroups, searchTargetGroups, UseCompileFramework);

                if(string.IsNullOrWhiteSpace(targetGroup) && AllowCompatibleFramework == true)
                {
                    // Depth search of compatible framework hierarchy, ie. netcoreapp1.1 => netstandard1.7 (compatible) => netstandard1.6 => ...
                    targetGroup = searchTargetGroups[originalTargetGroup].GetMetadata("CompatibleGroup");
                    targetGroup = FindCompatibleTargetGroup(targetGroup, supportedTargetGroups, searchTargetGroups);
                }

                metadata.Add("TargetGroup", targetGroup);
            }

            string[] supportedOSGroups = verticalGroups.Where(v => v.Contains(targetGroup)).Select(v => v.Split('_')[1]).ToArray();
            Dictionary<string, ITaskItem> searchOSGroups = OSGroups.ToDictionary(o => o.ItemSpec, o => o);
            string compatibleOSGroup = FindCompatibleOSGroup(osGroup, supportedOSGroups, searchOSGroups);
            metadata.Add("OSGroup", compatibleOSGroup);

            OutputItem = new TaskItem(Name, metadata);

            return true;
        }

        private string FindCompatibleOSGroup(string osGroup, string[] supportedOsGroups, Dictionary<string, ITaskItem> searchOSGroups)
        {
            if (string.IsNullOrWhiteSpace(osGroup))
            {
                return osGroup;
            }
            if (supportedOsGroups.Contains(osGroup))
            {
                return osGroup;
            }

            return FindCompatibleOSGroup(searchOSGroups[osGroup].GetMetadata("FallbackGroup"), supportedOsGroups, searchOSGroups);
        }

        private string FindCompatibleTargetGroup(string targetGroup, string [] supportedTargetGroups, Dictionary<string, ITaskItem> searchTargetGroups, bool UseCompileFramework = false)
        {
            if(string.IsNullOrWhiteSpace(targetGroup))
            {
                return targetGroup;
            }

            if (UseCompileFramework && searchTargetGroups.ContainsKey(targetGroup))
            {
                if (searchTargetGroups[targetGroup].GetMetadata("CompatibleGroup") != string.Empty)
                {
                    targetGroup = searchTargetGroups[targetGroup].GetMetadata("CompatibleGroup");
                }
            }

            if (supportedTargetGroups.Contains(targetGroup))
            {
                return targetGroup;
            }
            return FindCompatibleTargetGroup(searchTargetGroups[targetGroup].GetMetadata("FallbackGroup"), supportedTargetGroups, searchTargetGroups);
        }
    }
}
