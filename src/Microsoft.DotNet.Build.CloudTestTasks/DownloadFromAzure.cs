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
    public sealed class DownloadFromAzure : Task
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
        /// The name of the container to access.  The specified name must be in the correct format, see the
        /// following page for more info.  https://msdn.microsoft.com/en-us/library/azure/dd135715.aspx
        /// </summary>
        [Required]
        public string ContainerName { get; set; }

        /// <summary>
        /// An item group of blob filenames to download.  
        /// </summary>
        [Required]
        public ITaskItem[] Items { get; set; }

        /// <summary>
        /// Directory to download blob files to
        /// </summary>
        public string DownloadDirectory { get; set; }

        /// <summary>
        /// Indicates if the destination blob should be overwritten if it already exists.  The default if false.
        /// </summary>
        public bool Overwrite { get; set; }

        public override bool Execute()
        {
            DownloadDirectory = DownloadDirectory ?? Environment.CurrentDirectory;
            if(!Directory.Exists(DownloadDirectory))
            {
                Directory.CreateDirectory(DownloadDirectory);
            }
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", AccountName, AccountKey));
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(ContainerName);

            List<IListBlobItem> blobs = container.ListBlobs(null, true)?.ToList();

            foreach(IListBlobItem blob in blobs)
            {
                Log.LogMessage("Downloading URI - {0}", blob.Uri);
                CloudBlob cb = new CloudBlob(blob.Uri, storageAccount.Credentials);
                string filename = Path.Combine(DownloadDirectory, Path.GetFileName(blob.Uri.AbsolutePath));
                cb.DownloadToFile(filename, FileMode.Create);
            }

            return true;
        }
    }
}
