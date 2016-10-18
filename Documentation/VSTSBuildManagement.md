Note that this document relates to checking in existing build definitions which we are currently utilizing.  This document does not cover creating new build definitions in this model.

#### Summary

I would like to create a stand-alone package in BuildTools named Microsoft.DotNet.Builds.VSTSBuildsApi that would allow us to group build definitions together and, given a checked in build definition, it would create or update the grouped VSTS build definition and return that build definition's id for use in a build orchestration tool. 

#### <a id="CheckInDefinitionsProposal"></a>Checked-in Build Definitions

VSTS REST API's provide a mechanism whereby you can create, update, retrieve, and list VSTS build / release definitions in JSON format.  Provided local VSTS JSON formatted build definitions, an orchestrator should be able to query VSTS to either create or update the relevant build / release definition.

Build definitions should be checked-in to a repo.   The REST API's are full-fledged enough to allow us to query for build definitions, update build definitions, and create build definitions based on json text. A key piece is how we identify definitions.  We need some method to uniquely identify build definition files for a given repo / branch.  

**Checked-in Definitions Implementation**

Implementation options for supporting checked in build definitions include:

1. A web service

2. A stand-alone package

In either scenario, we provide a multiplexing mechanism which is a layer between an orchestrator and VSTS.  My preference is to provide that layer via a stand-alone package.  As a stand-alone package, we add the package to our orchestrator, and utilize its surface area to deliver VSTS builds.  My preference is for a stand-alone package because I think that the investment in an orchestrator from us is equivalent as what would be required for a web-service, but I don't want to own maintaining a web-service and dealing with that additional layer of service requirement for the foreseeable future.

I'd like to create a package, in BuildTools, named Microsoft.DotNet.Build.VSTSBuildsApi. The public api surface area of this library would be:

```C#
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="collectionIdentifier"> grouping identifer for build definitions</param>
        /// <param name="credentials">credentials used for basic rest api authentication</param>
        public VSTSBuilds(string collectionIdentifier, string credentials)

        /// <summary>
        /// Load a local build definition, combine the uniqueIdentifier with the definition name, 
        /// check VSTS for a matching definition name, if present, update that definition with the
        /// local build definition, otherwise create a new definition.
        /// </summary>
        /// <param name="stream">Stream to a VSTS build definition</param>
        /// <returns>Created or updated build Id</returns>
        public string CreateOrUpdateDefinition(Stream stream)
```

A typical usage would be...

```C#
        VSTSBuildsApi.VSTSBuilds buildsManager = new VSTSBuildsManager.VSTSBuilds(collectionId, CredentialsManager.GetAuthenticationHeaderCredentials(this));
        DefinitionId = buildsManager.CreateOrUpdateDefinition(DefinitionPath);
```

Given a definition path, VSTSBuildsApi would:

1. load the local build definition
2. join the `collectionId` with the definition name to create a unique VSTS definition name
3. look in VSTS for that definition name
4. create or update the definition
5. return the discovered or new definition Id which the orchestrator usese for queuing builds.

**Grouping build definitions with a collectionId**

We should be able to have checked-in, named, build definitions.  When an orchestrator picks up that local build definition and queries VSTS, it will need to correlate a group of build definitions in a way that logically makes sense. There may be multiple build definitions with the same name representing different releases, or build orchestration instances. 

Here are a couple of options for uniquely identifying a build definition

- Make the VSTS definition name be a combination of the name, repo path, and branch that they represent.
  - Example: for a checked in build definition, DotNet-CoreFx-Trusted-Windows-Native.json, the corresponding VSTS definition targetting dotnet/corefx:master would be corefx_master_DotNet-CoreFx-Trusted-Windows-Native.

- Provide a unique identifier from the orchestrator instance.
  - For PipeBuild, if PipeBuild is being scheduled from VSTS, then you can use the definition ID from the PipeBuild definition.
  - Example: for a checked in build definition, DotNet-CoreFx-Trusted-Windows-Native.json, the corresponding VSTS definition would be 123456_DotNet-CoreFx-Trusted-Windows-Native.

- Don't uniquely identify a build definition, just always create a new one.

My preference is for option (2).  Using an identifier provides a hook so that we can relate any build definition back to the instance which spawned it (something which is often difficult to do).  This would also allow us to have multiple build systems utilizing the infrastructure which would be less likely to encounter name collisions, ie greater flexibility.

**Build definition repositories**

The two obvious choices for checked in build definition locations are:

1. VSTS Git - This is a good choice because it provides a secure location for build definitions.  Negatives, must be cognizant of corresponding branch changes and ensuring that build definitions are branched in sync with those changes.

2. GitHub - This is a nice choice because it has the benefit of evolving / moving, along with the code it is tied to.  On the negative side, it forces every dev to carry these definitions with in their enlistment them when 99.9% of devs will not need or care about them.

My preference is for option 1.  There is a small maintenance cost which we will have to pay, but we should be cognizant of changes in this area anyways.  We're still able to branch / fork build definitions in sync with product changes. 

**REST API Contract support**

The "VSTSBuildsApi" library will support The [TFS build contract for a build definition](https://www.visualstudio.com/en-us/docs/integrate/extensions/reference/client/api/tfs/build/contracts/builddefinition) used by VSTS REST API version 2.0.  Checked in build definitions must conform to this spec to be supported. 






