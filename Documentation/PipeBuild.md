## Proposal for PipeBuild

PipeBuild is our "Official Build" orchestration tool.  The tool defines pipelines which consist of various builds required for that pipeline to complete.  For our purposes, "builds" are VSTS build or release definitions.  Pipelines may optionally contain dependencies on other pipelines passing successfully before they are queued to run.  Our current version of PipeBuild has a lot of benefits, but there are some glaring weaknesses which we should address.

### Issues with PipeBuild

- PipeBuild is not versioned.  

  - There is no way to make breaking changes without breaking anybody currently utilizing the system.

  - Servicing branches which use PipeBuild are required to be updated to conform to the latest version of PipeBuild, this is not sustainable.

- .NET Core PipeBuild usage has a heavy reliance on VSTS build / release definitions.  The VSTS coupling provides benefits such as keeping secrets, managing machine pools, queueing builds, and reporting; but there are some difficult areas in our current usage which could be improved.

  - Build / release definitions are not versioned

  - Updating definitions is an error-prone process
  
### PipeBuild Changes Proposal

- [Make PipeBuild a NuGet Package](#NuGetPackageProposal)

- [Decouple Pipeline and Build Definitions from PipeBuild](#DecoupleDefinitionsProposal)

- [Checked in Build Definitions](#CheckInDefinitionsProposal)

- [Move Sources to GitHub](#GitHubProposal)

If you're not interested in the details, the [TL;DR](#ProposalsSummary) is next.

#### <a id="ProposalsSummary"></a>Proposed Changes Summary (TL;DR)

- Move PipeBuild into BuildTools where it will be delivered as a package
- Decouple pipelines / definitions from PipeBuild source and give repo owners more control over themselves
  - More control includes owning the definitions themselves as json files which may be checked in or distributed however the owner desires
    - I'd expect most repo owners to keep pipeline / build definitions either in their repo, or in a companion repo, the point being that the definitions themselves are versioned controlled via git / branching mechanics and consuming those definitions would just be cloning a repo at a specific commit (or branch).
- Create a separate PipeBuild launcher repo in Git which contains the BuildTools bootstrapping, plus a script which updates the version of the PipeBuild package which is used.

The following, are the details.

#### <a id="NuGetPackageProposal"></a>Make PipeBuild a NuGet Package

Delivering PipeBuild as a NuGet package will allow us to version our PipeBuild releases, and permit consumers to be explicit about which verison of PipeBuild they rely on.

Packaging PipeBuild solves the "versioning" problem, but we also need a mechanism which makes it simple for consumers to use the tool from a simple command without jumping through hoops. 

*Proposed solutions*

Update PipeBuild to build as a package using similar mechanisms that we use for BuildTools, including publishing to MyGet.

With PipeBuild delivered as a package, we need to provide a way to use that package easily.  Two possible solutions for this are:

1. Use the bootstrap tooling which is already implemented in BuildTools.  We can create a barebones repo (eg. PipeBuildInstaller), which contains the bootstrap tooling tailored towards the PipeBuild package, plus a script which updates ".toolsversion" and executes the bootstrap process.  I'd probably want this barebones repo to live in Git where we can more tightly control access to it than we could on GitHub.
 
   Positives: 

   - We're using the same bootstrap mechanisms we use in BuildTools

   Negatives:

   - Bootstrap might still be more than we need since we don't require x-plat capability for PipeBuild.  ie, we don't need cli or any of the related x-plat scripting

2. Create a barebones wrapper project which passes through to PipeBuild.  Provide a script which writes out a project.json file with an entry for the version of PipeBuild that we want to build / run.

   The project would consist of:
   
   - PipeBuildLauncher.csproj

   - project.json

```json
{
  "dependencies": {
    "Microsoft.DotNet.PipeBuild": "1.0.0"
  },
  "frameworks": {
    "blah"
  }
}
```

   - program.cs

```C#
namespace PipeBuildLauncher
{
  class Program
  {
    static void Main(string[] args)
    {
      PipeBuild.Program.Launch(args);
    }
  }
}
```

   - build.script [PipeBuild version]

     - generates project.json with [PipeBuild version]
     - builds PipeBuildLauncher.json

    Positives:
    - Self-contained launcher scoped for PipeBuild makes it easy to launch any version of PipeBuild we care about.

    Negatives:
    - It is its own thing, one more piece of technology that we need to maintain (though maintenance cost should be low).

My preference is to use the bootstrapping because I really want to limit the amount of technology we own if it's not necessary. 

#### <a id="DecoupleDefinitionsProposal"></a>Decouple Pipeline and Build Definitions from PipeBuild

Information about pipelines and VSTS build definitions are currently stored in the PipeBuild repo for each consumer.  Consumers should manage their own pipelines / build definitions.  PipeBuild should provide a mechanism whereby a consumer provides this information to PipeBuild or tells PipeBuild how to access it, but individual consumers manage the information themselves.

*Proposed solutions*

The PipeBuild tool should take a pointer to a pipeline.json file.  Additional required pieces of information (such as `definitions.json`) should be discovered via entries in the pipeline.json file.  This is a slight change over today, where definitions.json is hard-coded in the tool.

#### <a id="CheckInDefinitionsProposal"></a>Checked in Build Definitions

VSTS REST API's provide a mechanism whereby you can create, update, retrieve, and list VSTS build / release definitions in JSON format.  If PipeBuild is provided local VSTS JSON formatted build definitions, PipeBuild should be able to query VSTS to either create or update the relevant build / release definition.

*Proposed solutions*

Build definitions should be checked-in to a repo.  A pipeline.json file can provide data to where those checked in build definitions it consumes are located. The REST API's are full-fledged enough to allow us to query for build definitions, update build definitions, and create build definitions based on json text. A key piece is how we identify definitions.  We need some method to uniquely identify build definition files for a given repo / branch.  

ie.  We should be able to have checked in build definitions named "Build-Windows-Native".  When PipeBuild picks up that build definition and queries VSTS for it though, it will need some way to correlate the current instance of PipeBuild with a definition, because multiple different instances of PipeBuild in multiple different repo's / branches, may have checked in build definitions named "Build-Windows-Native". 

Here are a couple of options for uniquely identifying a build definition

1. Make the VSTS definition name be a combination of the name, repo path, and branch.

2. Provide a unique identifier from the PipeBuild instance.  If PipeBuild is being scheduled from VSTS, then you can use the definition ID from the PipeBuild definition.

3. Don't uniquely identify a build definition, just always create a new one.

My preference is for option (2).  It ties running PipeBuild to VSTS, but for our purposes, we shouldn't be running PipeBuild outside of VSTS, and we certainly shouldn't be giving permissions to update / generate / etc, build definitions.  In addition, using this key provides a hook so that we can relate any build definition back to the instance which spawned it (something which is difficult to do with today's model). 

#### <a id="GitHubProposal"></a>Move Sources to GitHub

Transitioning to a versioned / packaged version of PipeBuild is a major split from the current incarnation of PipeBuild.  I propose that we fork the sources for PipeBuild onto a GitHub repo.  Current PipeBuild consumers may continue to use PipeBuild as they are used to, but we don't make additional feature changes to that version of PipeBuild.  All feature changes, going forward, would occur in the packaged PipeBuild version produced from a GitHub repo.  There is nothing inherently secret about PipeBuild, and it does not manage any secrets.

*Proposed solutions*

1. Create a new repo for PipeBuild on GitHub.
  
   Positives
   - Allows fine control over builds / releases / branching / etc...

   Negatives
   - Owning another repo

2. Move PipeBuild to BuildTools

   Positives
   - Infrastructure already in place for building packages
   - There are already BuildTools contributors looking to solve problems such as releases, branching, etc...

   Negatives
   - Less control

3. Leave PipeBuild in Git

My preference is to move PipeBuild into BuildTools.  Moving PipeBuild to an existing repo with active development, which is already geared towards distributing this kind of package, provides great benefit vs leaving PipeBuild in its own development island.

