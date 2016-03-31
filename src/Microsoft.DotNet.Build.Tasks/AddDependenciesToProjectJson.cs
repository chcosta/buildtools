using Microsoft.Build.Utilities;
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
    /// <summary>
    /// Parse a project.json, and add additional dependencies, then write a out new project.json.
    /// </summary>
    public class AddDependenciesToProjectJson : Task
    {
        private Regex VersionStructureRegex = new Regex(@"(\d+\.\d+\.\d+)-((beta|rc2|rc3)?-?(\d+(-\d\d)?))");
        private Regex BuildNumberOverrideStructureRegex = new Regex(@"(beta|rc2|rc3)?-?(\d+(-\d\d)?)?");
        private Regex ThreePartVersionRegex = new Regex(@"(\d+\.\d+\.\d+)");

        // Additional Dependencies to add to the project.json. May Optionally contain a version.
        // Will Override dependencies present in the project if there is a conflict.
        // AdditionalDependencies required metadata:  Name, Version

        [Required]
        public ITaskItem[] AdditionalDependencies { get; set; }

        // Permit overriding package versions found in project.json with custom build number version.
        public string PackageBuildNumberOverride { get; set; }

        // Original package version which is used to seed the output project.json
        [Required]
        public String ProjectJson { get; set; }

        // The directory to put the generated project.json in
        [Required]
        public String OutputProjectJson { get; set; }

        public override bool Execute()
        {
            if (!File.Exists(ProjectJson))
            {
                Log.LogError("Cannot find specified project.json - '{0}'", ProjectJson);
                return false;
            }
            if(!string.IsNullOrWhiteSpace(PackageBuildNumberOverride))
            {
                if(!BuildNumberOverrideStructureRegex.IsMatch(PackageBuildNumberOverride))
                {
                    Log.LogError("Invalid package version format: '{0}'", PackageBuildNumberOverride);
                    return false;
                }
            }

            JObject projectRoot = ReadProject(ProjectJson);
            JObject dependencies = GenerateDependencies(projectRoot);
            projectRoot = UpdateProperty("dependencies", projectRoot, dependencies);
            WriteProject(projectRoot, OutputProjectJson);

            return true;
        }

        private static JObject ReadProject(string projectJsonPath)
        { 
           using (TextReader projectFileReader = File.OpenText(projectJsonPath)) 
           { 
              var projectJsonReader = new JsonTextReader(projectFileReader); 
              var serializer = new JsonSerializer(); 
              return serializer.Deserialize<JObject>(projectJsonReader); 
           } 
        }

        // Generate the combines dependencies from the projectjson jObject and from AdditionalDependencies
        private JObject GenerateDependencies(JObject projectJsonRoot)
        {
            var originalDependenciesList = new List<JToken>();
            var returnDependenciesList = new List<JToken>();
            originalDependenciesList = projectJsonRoot["dependencies"]?.Children().ToList();

            // No PackageBuildNumberOverride was specified, so try to find one to associate with our AdditionalDependencies
            PackageBuildNumberOverride = PackageBuildNumberOverride ?? DeriveBuildNumber(originalDependenciesList);

            // Update versions in dependencies
            foreach(JProperty property in originalDependenciesList.Select(od => od))
            {
                string version = property.Value.ToString();
                Match m = VersionStructureRegex.Match(version);
                if (m.Success)
                {
                    version = m.Groups[1].Value + "-" + PackageBuildNumberOverride;
                }

                // Only add the original dependency if it wasn't passed as an AdditionalDependency, ie. AdditionalDependencies may override dependencies in project.json
                if (AdditionalDependencies?.Where(d => d.GetMetadata("Name").Equals(property.Name, StringComparison.OrdinalIgnoreCase)).Count() == 0)
                {
                    JProperty addProperty = new JProperty(property.Name, version);
                    returnDependenciesList.Add(addProperty);
                }

            }

            foreach (var dependency in AdditionalDependencies)
            {
                string name = dependency.GetMetadata("Name");
                // Don't add a new dependency if one already exists.
                if (returnDependenciesList.Count(rd => ((JProperty)rd).Name.Equals(name)) == 0)
                {
                    string version = GetThreePartVersion(dependency.GetMetadata("Version"));
                    if (!string.IsNullOrWhiteSpace(PackageBuildNumberOverride))
                    {
                        version += "-" + PackageBuildNumberOverride;
                    }
                    JProperty property = new JProperty(name, version);
                    returnDependenciesList.Add(property);
                }
                else
                {
                    Log.LogMessage("Ignoring AdditionalDependency '{0}', dependency is already present in {1}", name, ProjectJson);
                }
            }
            
            return new JObject(returnDependenciesList.ToArray());
        }

        /* No build number was specified, determine the build number by examining the other packages in the dependencies list */
        private string DeriveBuildNumber(List<JToken> dependenciesList)
        {
            foreach (JProperty property in dependenciesList.Select(dl => (JProperty)dl))
            {
                string version = property.Value.ToString();
                Match m = VersionStructureRegex.Match(version);
                if (m.Success)
                {
                    string buildNumber = m.Groups[2].Value;
                    Log.LogMessage("Determined buildnumber using existing package dependencies as '{0}'", buildNumber);
                    return buildNumber;
                }
            }
            return PackageBuildNumberOverride;
        }

        /* Given a project.json as a JObject, replace it's dependencies property with a new dependencies property. */
        private JObject UpdateProperty(string propertyName, JObject projectJsonRoot, JObject updatedProperties)
        {
            if (projectJsonRoot.Property(propertyName) != null)
            {
                JObject returnJsonRoot = new JObject();
                Dictionary<string, JToken> properties = new Dictionary<string, JToken>();

                // Collect all properties from jObject which are not the dependencies property
                foreach (var property in projectJsonRoot.Properties().Where(p => !p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
                {
                        properties.Add(property.Name, property.Value);
                }
                // Add new dependencies to our jObject
                returnJsonRoot[propertyName] = updatedProperties;

                // Add back all of the properties we collected from previous jObject
                foreach (string property in properties.Keys)
                {
                    returnJsonRoot.Add(property, properties[property]);
                }
                return returnJsonRoot;
            }
            return projectJsonRoot;
        }


        private string GetThreePartVersion(string version)
        {
            if (ThreePartVersionRegex.IsMatch(version))
                return ThreePartVersionRegex.Match(version).Value;
            return version;
        }

        private static void WriteProject(JObject projectRoot, string projectJsonPath)
        {
            string projectJson = JsonConvert.SerializeObject(projectRoot, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(projectJsonPath));
            File.WriteAllText(projectJsonPath, projectJson + Environment.NewLine); 
        }
    }
}
