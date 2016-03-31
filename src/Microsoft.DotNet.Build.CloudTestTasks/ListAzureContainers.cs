// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.DotNet.Build.CloudTestTasks
{
    public sealed class ListAzureContainers : Task
    {
        /// <summary>
        /// The Azure account name used when creating the connection string.
        /// </summary>
        [Required]
        public string AccountName { get; set; }

        /// <summary>
        /// The Azure account key used when creating the connection string.
        /// </summary>
        [Required]
        public string AccountKey { get; set; }

        /// <summary>
        /// Prefix of Azure containers desired to return;
        /// </summary>
        public string Prefix { get; set; }

        /// <summary>
        /// An item group of blob filenames to download.  
        /// </summary>
        [Output]
        public ITaskItem[] ContainerNames { get; set; }

        public override bool Execute()
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", AccountName, AccountKey));
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            IEnumerable<CloudBlobContainer> containers = blobClient.ListContainers(Prefix);
            ContainerNames = containers.Select(c => new TaskItem(c.Name)).ToArray();
            return true;
        }
    }
}
