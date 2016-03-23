using Microsoft.Build.Utilities;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.DotNet.Build.Tasks
{
    public class EvaluateMSBuildProject : Task
    {
        // The name of a project file to evaluate
        [Required]
        public String ProjectFile { get; set; }

        // Properties to pass for the project evaluation.
        public ITaskItem[] AdditionalProperties { get; set; }

        // Specify a single property whose value you want returned from the project, just a nice shortcut to get one particular evaluated property.
        public string RequestProperty { get; set; }

        // The requested property if one was specified and found.
        [Output]
        public string RequestedProperty { get; set; }

        // All evaluated items from the project
        // Return an Item (item type) with this metadata
        //   <MetadataName>Metadata value</MetadataName>
        //   ...
        //   <MetadataName>Metadata value</MetadataName>
        [Output]
        public ITaskItem[] ProjectItems { get; set; }

        // All evaluated properties from the project
        // Returns an Item (property name) with this metadata
        //   <Name>property name</Name>
        //   <Value>property value</Value>
        [Output]
        public ITaskItem[] ProjectProperties { get; set; }

        public override bool Execute()
        {
            var properties = new Dictionary<string, string>();
            if (AdditionalProperties != null)
            {
                foreach (ITaskItem item in AdditionalProperties)
                {
                    foreach (string name in item.MetadataNames)
                    {
                        properties.Add(name, item.GetMetadata(name));
                    }
                }
            }
            var collection = new ProjectCollection(properties);
            var project = collection.LoadProject(ProjectFile);
            var items = project.Items;
            var projectProperties = project.Properties;
            List<ITaskItem> updatedProjectProperties = new List<ITaskItem>();
            foreach(var property in projectProperties)
            {
                ITaskItem projectProperty = new TaskItem(property.Name);
                projectProperty.SetMetadata("Name", property.Name);
                projectProperty.SetMetadata("Value", property.EvaluatedValue);
                updatedProjectProperties.Add(projectProperty);
                if(property.Name.Equals(RequestProperty, StringComparison.OrdinalIgnoreCase))
                {
                    if(RequestedProperty == null)
                    {
                        RequestedProperty = property.EvaluatedValue;
                    }
                    else
                    {
                        Log.LogWarning("Requested property '{0}' returned multiple results.", RequestProperty);
                    }
                }
            }
            ProjectProperties = updatedProjectProperties.ToArray();

            List<ITaskItem> projectItems = new List<ITaskItem>();
            foreach(var item in items)
            {
                ITaskItem projectItem = new TaskItem(item.ItemType);
                foreach(var metadata in item.Metadata)
                {
                    projectItem.SetMetadata(metadata.Name, metadata.EvaluatedValue);
                }
                projectItems.Add(projectItem);
            }
            ProjectItems = projectItems.ToArray();

            return true;
        }
    }
}
