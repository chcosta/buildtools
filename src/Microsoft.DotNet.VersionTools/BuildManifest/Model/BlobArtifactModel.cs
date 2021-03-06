// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Xml.Linq;

namespace Microsoft.DotNet.VersionTools.BuildManifest.Model
{
    public class BlobArtifactModel
    {
        private static readonly string[] AttributeOrder =
        {
            nameof(Id)
        };

        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();

        public string Id
        {
            get { return Attributes[nameof(Id)]; }
            set { Attributes[nameof(Id)] = value; }
        }

        public override string ToString() => $"Blob {Id}";

        public XElement ToXml() => new XElement(
            "Blob",
            Attributes.CreateXmlAttributes(AttributeOrder));

        public static BlobArtifactModel Parse(XElement xml) => new BlobArtifactModel
        {
            Id = xml.GetRequiredAttribute(nameof(Id)),
            Attributes = xml.CreateAttributeDictionary()
        };
    }
}
