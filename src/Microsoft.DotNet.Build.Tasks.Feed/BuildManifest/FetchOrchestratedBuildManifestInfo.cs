﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.VersionTools.Automation;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;
using Microsoft.DotNet.VersionTools.BuildManifest;
using Microsoft.DotNet.VersionTools.BuildManifest.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Microsoft.DotNet.Build.Tasks.Feed.BuildManifest
{
    public class FetchOrchestratedBuildManifestInfo : Task
    {
        [Required]
        public string VersionsRepoPath { get; set; }

        public string GitHubAuthToken { get; set; }
        public string GitHubUser { get; set; }
        public string GitHubEmail { get; set; }

        public string VersionsRepo { get; set; }
        public string VersionsRepoOwner { get; set; }

        public string VersionsRepoRef { get; set; }

        [Output]
        public string OrchestratedBuildId { get; set; }

        [Output]
        public string OrchestratedIdentity { get; set; }

        [Output]
        public ITaskItem[] OrchestratedBlobFeed { get; set; }

        [Output]
        public ITaskItem[] OrchestratedBlobFeedArtifacts { get; set; }

        public override bool Execute()
        {
            GitHubAuth gitHubAuth = null;
            if (!string.IsNullOrEmpty(GitHubAuthToken))
            {
                gitHubAuth = new GitHubAuth(GitHubAuthToken, GitHubUser, GitHubEmail);
            }
            using (var gitHubClient = new GitHubClient(gitHubAuth))
            {
                var client = new BuildManifestClient(gitHubClient);

                OrchestratedBuildModel manifest = client.FetchManifestAsync(
                    new GitHubProject(VersionsRepo, VersionsRepoOwner),
                    VersionsRepoRef,
                    VersionsRepoPath)
                    .Result;

                EndpointModel[] orchestratedFeeds = manifest.Endpoints
                    .Where(e => e.IsOrchestratedBlobFeed)
                    .ToArray();

                if (orchestratedFeeds.Length != 1)
                {
                    throw new Exception(
                        "Invalid manifest. Expected 1 orchestrated blob feed, " +
                        $"found {orchestratedFeeds.Length}.");
                }

                EndpointModel feed = orchestratedFeeds[0];

                IEnumerable<ITaskItem> packageItems = feed.Artifacts.Packages.Select(CreateItem);
                IEnumerable<ITaskItem> blobItems = feed.Artifacts.Blobs.Select(CreateItem);

                OrchestratedBuildId = manifest.Identity.BuildId;
                OrchestratedIdentity = manifest.Identity.ToString();
                OrchestratedBlobFeed = new[] { new TaskItem("Endpoint", feed.Attributes) };
                OrchestratedBlobFeedArtifacts = packageItems.Concat(blobItems).ToArray();
            }

            return !Log.HasLoggedErrors;
        }

        private ITaskItem CreateItem(BlobArtifactModel model)
        {
            return new TaskItem("Blob", ArtifactMetadata(model.ToXml(), model.Attributes));
        }

        private ITaskItem CreateItem(PackageArtifactModel model)
        {
            return new TaskItem("Package", ArtifactMetadata(model.ToXml(), model.Attributes));
        }

        private Dictionary<string, string> ArtifactMetadata(
            XElement artifactXml,
            IDictionary<string, string> attributes)
        {
            return new Dictionary<string, string>(attributes)
            {
                [UpdateOrchestratedBuildManifest.XmlMetadataName] = artifactXml.ToString()
            };
        }
    }
}
