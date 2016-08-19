﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.DotNet.VersionTools;
using Microsoft.DotNet.VersionTools.Automation;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;
using Microsoft.DotNet.VersionTools.Util;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.DotNet.Build.Tasks.VersionTools
{
    public class UpdateDependenciesAndSubmitPullRequest : BaseDependenciesTask
    {
        private const string s_currentRef = "CurrentRef";

        public string ProjectRepoOwner { get; set; }

        [Required]
        public string ProjectRepoName { get; set; }
        [Required]
        public string ProjectRepoBranch { get; set; }

        [Required]
        public string GitHubAuthToken { get; set; }
        public string GitHubUser { get; set; }
        public string GitHubEmail { get; set; }

        /// <summary>
        /// The git author of the update commit. Defaults to the same as GitHubUser.
        /// </summary>
        public string GitHubAuthor { get; set; }

        public ITaskItem[] NotifyGitHubUsers { get; set; }

        public string CurrentRefXmlPath { get; set; }

        public bool AlwaysCreateNewPullRequest { get; set; }

        protected override void TraceListenedExecute()
        {
            // Use the commit sha of versions repo master (not just "master") for stable upgrade.
            var gitHubAuth = new GitHubAuth(GitHubAuthToken, GitHubUser, GitHubEmail);
            var client = new GitHubClient(gitHubAuth);
            string masterSha = client
                .GetReferenceAsync(new GitHubProject("versions", "dotnet"), "heads/master")
                .Result.Object.Sha;

            foreach (ITaskItem item in DependencyBuildInfo)
            {
                if (!string.IsNullOrEmpty(item.GetMetadata(s_currentRef)))
                {
                    item.SetMetadata(s_currentRef, masterSha);
                }
            }

            DependencyUpdateResults updateResults = DependencyUpdateUtils.Update(
                CreateUpdaters().ToArray(),
                CreateBuildInfoDependencies().ToArray());

            if (!string.IsNullOrEmpty(CurrentRefXmlPath))
            {
                // Update the build info commit sha for each applicable build info used.
                foreach (BuildInfo info in updateResults.UsedBuildInfos)
                {
                    ITaskItem infoItem = FindDependencyBuildInfo(info.Name);
                    if (string.IsNullOrEmpty(infoItem.GetMetadata(s_currentRef)))
                    {
                        continue;
                    }

                    Regex upgrader = CreateXmlUpdateRegex($"{info.Name}{s_currentRef}", s_currentRef);

                    Action replace = FileUtils.ReplaceFileContents(
                        CurrentRefXmlPath,
                        contents =>
                        {
                            Match m = upgrader.Match(contents);
                            Group g = m.Groups[s_currentRef];

                            return contents
                                .Remove(g.Index, g.Length)
                                .Insert(g.Index, masterSha);
                        });
                    replace();
                }
            }

            if (updateResults.ChangesDetected())
            {
                var origin = new GitHubProject(ProjectRepoName, GitHubUser);

                var upstreamBranch = new GitHubBranch(
                    ProjectRepoBranch,
                    new GitHubProject(ProjectRepoName, ProjectRepoOwner));

                string suggestedMessage = updateResults.GetSuggestedCommitMessage();
                string body = string.Empty;
                if (NotifyGitHubUsers != null)
                {
                    body += PullRequestCreator.NotificationString(NotifyGitHubUsers.Select(item => item.ItemSpec));
                }

                var prCreator = new PullRequestCreator(gitHubAuth, origin, upstreamBranch, GitHubAuthor);
                prCreator.CreateOrUpdateAsync(
                    suggestedMessage,
                    suggestedMessage + $" ({ProjectRepoBranch})",
                    body,
                    forceCreate: AlwaysCreateNewPullRequest).Wait();
            }
            else
            {
                Log.LogMessage("No update required: no changes detected.");
            }
        }

        private ITaskItem FindDependencyBuildInfo(string name)
        {
            return DependencyBuildInfo.SingleOrDefault(item => item.ItemSpec == name);
        }
    }
}
