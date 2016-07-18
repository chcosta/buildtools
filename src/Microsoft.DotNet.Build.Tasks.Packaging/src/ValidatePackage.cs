// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PropertyNames = NuGet.Client.ManagedCodeConventions.PropertyNames;

namespace Microsoft.DotNet.Build.Tasks.Packaging
{
    public class ValidatePackage : PackagingTask
    {
        [Required]
        public string ContractName
        {
            get;
            set;
        }

        [Required]
        public string PackageId
        {
            get;
            set;
        }

        [Required]
        public string PackageVersion
        {
            get;
            set;
        }

        /// <summary>
        /// List of files in the package.
        ///   Identity: source path
        ///   TargetPath: path inside package
        ///   TargetFramework: target moniker of asset
        ///   Package: In the case that we are considering multiple packages
        ///            supporting the same contract, this will distinguish
        ///            files coming from other packages and in a different
        ///            resolution scope.
        ///            All pacakges will be considered when validating 
        ///            support and checking for binclashes.
        /// </summary>
        [Required]
        public ITaskItem[] Files
        {
            get;
            set;
        }

        /// <summary>
        /// Frameworks supported by this package
        ///   Identity: name of framework, can suffx '+' to indicate all later frameworks under validation.
        ///   RuntimeIDs: Semi-colon seperated list of runtime IDs.  If specified overrides the value specified in Frameworks.
        ///   Version: version of API supported
        /// </summary>
        [Required]
        public ITaskItem[] SupportedFrameworks
        {
            get;
            set;
        }

        /// <summary>
        /// Frameworks to evaluate.
        ///   Identity: Framework
        ///   RuntimeIDs: Semi-colon seperated list of runtime IDs
        /// </summary>
        [Required]
        public ITaskItem[] Frameworks
        {
            get;
            set;
        }

        /// <summary>
        /// Path to runtime.json that contains the runtime graph.
        /// </summary>
        [Required]
        public string RuntimeFile
        {
            get;
            set;
        }

        public string SuppressionFile
        {
            get;
            set;
        }


        [Required]
        public string FrameworkListsPath
        {
            get;
            set;
        }

        public bool SkipGenerationCheck
        {
            get;
            set;
        }

        public bool SkipSupportCheck
        {
            get;
            set;
        }

        public bool UseNetPlatform
        {
            get { return _generationIdentifier == FrameworkConstants.FrameworkIdentifiers.NetPlatform; }
            set { _generationIdentifier = value ? FrameworkConstants.FrameworkIdentifiers.NetPlatform : FrameworkConstants.FrameworkIdentifiers.NetStandard; }
        }

        /// <summary>
        /// List of frameworks which were validated and determined to be supported
        ///   Identity: framework short name
        ///   Framework: framework full name
        ///   Version: assembly version of API that is supported
        ///   Inbox: true if assembly is expected to come from targeting pack
        ///   ValidatedRIDs: all RIDs that were scanned
        /// </summary>
        [Output]
        public ITaskItem[] AllSupportedFrameworks
        {
            get;
            set;
        }

        /// <summary>
        /// JSON file describing results of validation
        /// </summary>
        public string ValidationReport {
            get;
            set;
        }

        /// <summary>
        /// property bag of error suppressions
        /// </summary>
        private Dictionary<Suppression, HashSet<string>> _suppressions;
        private Dictionary<string, List<PackageItem>> _validateFiles;
        private Dictionary<NuGetFramework, ValidationFramework> _frameworks;
        private FrameworkSet _frameworkSet;
        private AggregateNuGetAssetResolver _resolver;
        private Dictionary<string, PackageItem> _targetPathToPackageItem;
        private string _generationIdentifier = FrameworkConstants.FrameworkIdentifiers.NetStandard;
        private static Version s_maxVersion = new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

        public override bool Execute()
        {
            LoadSuppressions();
            LoadFiles();
            LogPackageContent();
            LoadSupport();

            if (!SkipGenerationCheck)
            {
                ValidateGenerations();
            }

            if (!SkipSupportCheck)
            {
                // TODO: need to validate dependencies.
                ValidateSupport();
            }


            return !Log.HasLoggedErrors;
        }

        private void ValidateGenerations()
        {
            // get the generation of all portable implementation dlls.
            var allRuntimeGenerations = _resolver.GetAllRuntimeItems().Values
                .SelectMany(groups => groups.Select(group => group.Properties[PropertyNames.TargetFrameworkMoniker] as NuGetFramework))
                .Where(fx => fx != null && fx.Framework == _generationIdentifier)
                .Select(fx => fx.Version);

            // get the generation of all supported frameworks (some may have framework specific implementations
            // or placeholders).
            var allSupportedGenerations = _frameworks.Values.Where(vf => vf.SupportedVersion != null && FrameworkUtilities.IsGenerationMoniker(vf.Framework))
                .Select(vf => vf.Framework.Version);

            // find the minimum supported version as the minimum of any generation explicitly implemented 
            // with a portable implementation, or the generation of a framework with a platform specific
            // implementation.
            Version minSupportedGeneration = allRuntimeGenerations.Concat(allSupportedGenerations).Min();

            // validate API version against generation for all files
            foreach (var validateFile in _validateFiles.SelectMany(packageFileSet => packageFileSet.Value)
                .Where(f => IsDll(f.SourcePath) && FrameworkUtilities.IsGenerationMoniker(f.TargetFramework)))
            {
                if (validateFile.TargetFramework.Version < minSupportedGeneration)
                {
                    Log.LogError($"Invalid generation {validateFile.TargetFramework.Version} for {validateFile.SourcePath}, must be at least {minSupportedGeneration} based on the implementations in the package.  If you meant to target the lower generation you may be missing an implementation for a framework on that lower generation.  If not you should raise the generation of the reference assembly to match that of the lowest supported generation of all implementations/placeholders.");
                }
            }
        }
        private void ValidateSupport()
        {
            var runtimeFxSuppression = GetSuppressionValues(Suppression.PermitRuntimeTargetMonikerMismatch) ?? new HashSet<string>();
            ValidationReport report = null;
            if (ValidationReport != null)
            {
                report = CreateValidationReport();
            }

            // validate support for each TxM:RID
            foreach (var validateFramework in _frameworks.Values)
            {
                NuGetFramework fx = validateFramework.Framework;
                Version supportedVersion = validateFramework.SupportedVersion;

                var compileAssetPaths = _resolver.ResolveCompileAssets(fx, PackageId);
                bool hasCompileAsset, hasCompilePlaceHolder;
                ExamineAssets("Compile", ContractName, fx.ToString(), compileAssetPaths, out hasCompileAsset, out hasCompilePlaceHolder);

                if (report != null && validateFramework.RuntimeIds.All(rid => !String.IsNullOrEmpty(rid)))
                {
                    // Add Framework only (compile) target if all RIDs are non-empty.
                    // This acts as a compile target for a framework that requires a RID for runtime.
                    var reportTarget = new Target()
                    {
                        Framework = fx.ToString(),
                        RuntimeID = null,
                        CompileAssets = compileAssetPaths.Where(c => !NuGetAssetResolver.IsPlaceholder(c)).Select(c => GetPackageAssetFromTargetPath(c)).ToArray()
                    };
                    report.Targets.Add(fx.ToString(), reportTarget);
                }

                // resolve/test for each RID associated with this framework.
                foreach (string runtimeId in validateFramework.RuntimeIds)
                {
                    string target = String.IsNullOrEmpty(runtimeId) ? fx.ToString() : $"{fx}/{runtimeId}";
                    var runtimeAssetPaths = _resolver.ResolveRuntimeAssets(fx, runtimeId);

                    bool hasRuntimeAsset, hasRuntimePlaceHolder;
                    ExamineAssets("Runtime", ContractName, target, runtimeAssetPaths, out hasRuntimeAsset, out hasRuntimePlaceHolder);

                    if (null == supportedVersion)
                    {
                        // Contract should not be supported on this platform.
                        bool permitImplementation = HasSuppression(Suppression.PermitImplementation, target);

                        if (hasCompileAsset && (hasRuntimeAsset & !permitImplementation))
                        {
                            Log.LogError($"{ContractName} should not be supported on {target} but has both compile and runtime assets.");
                        }
                        else if (hasRuntimeAsset & !permitImplementation)
                        {
                            Log.LogError($"{ContractName} should not be supported on {target} but has runtime assets.");
                        }

                        if (hasRuntimePlaceHolder && hasCompilePlaceHolder)
                        {
                            Log.LogError($"{ContractName} should not be supported on {target} but has placeholders for both compile and runtime which will permit the package to install.");
                        }
                    }
                    else
                    {
                        if (report != null)
                        {
                            var reportTarget = new Target()
                            {
                                Framework = fx.ToString(),
                                RuntimeID = runtimeId,
                                CompileAssets = compileAssetPaths.Where(c => !NuGetAssetResolver.IsPlaceholder(c)).Select(c => GetPackageAssetFromTargetPath(c)).ToArray(),
                                RuntimeAssets = runtimeAssetPaths.Where(r => !NuGetAssetResolver.IsPlaceholder(r)).Select(r => GetPackageAssetFromTargetPath(r)).ToArray()
                            };
                            report.Targets.Add(target, reportTarget);
                        }

                        if (validateFramework.IsInbox)
                        {
                            if (!hasCompileAsset && !hasCompilePlaceHolder)
                            {
                                Log.LogError($"Framework {fx} should support {ContractName} inbox but was missing a placeholder for compile-time.  You may need to add <InboxOnTargetFramework Include=\"{fx.GetShortFolderName()}\" /> to your project.");
                            }
                            else if (hasCompileAsset)
                            {
                                Log.LogError($"Framework {fx} should support {ContractName} inbox but contained a reference assemblies: {String.Join(", ", compileAssetPaths)}.  You may need to add <InboxOnTargetFramework Include=\"{fx.GetShortFolderName()}\" /> to your project.");
                            }

                            if (!hasRuntimeAsset && !hasRuntimePlaceHolder)
                            {
                                Log.LogError($"Framework {fx} should support {ContractName} inbox but was missing a placeholder for run-time.  You may need to add <InboxOnTargetFramework Include=\"{fx.GetShortFolderName()}\" /> to your project.");
                            }
                            else if (hasRuntimeAsset)
                            {
                                Log.LogError($"Framework {fx} should support {ContractName} inbox but contained a implementation assemblies: {String.Join(", ", runtimeAssetPaths)}.  You may need to add <InboxOnTargetFramework Include=\"{fx.GetShortFolderName()}\" /> to your project.");
                            }
                        }
                        else
                        {
                            Version referenceAssemblyVersion = null;
                            if (!hasCompileAsset)
                            {
                                Log.LogError($"{ContractName} should be supported on {target} but has no compile assets.");
                            }
                            else
                            {
                                var referenceAssemblies = compileAssetPaths.Where(IsDll);

                                if (referenceAssemblies.Count() > 1)
                                {
                                    Log.LogError($"{ContractName} should only contain a single compile asset for {target}.");
                                }

                                foreach (var referenceAssembly in referenceAssemblies)
                                {
                                    referenceAssemblyVersion = _targetPathToPackageItem[referenceAssembly].Version;

                                    if (!VersionUtility.IsCompatibleApiVersion(supportedVersion, referenceAssemblyVersion))
                                    {
                                        Log.LogError($"{ContractName} should support API version {supportedVersion} on {target} but {referenceAssembly} was found to support {referenceAssemblyVersion?.ToString() ?? "<unknown version>"}.");
                                    }
                                }
                            }

                            if (!hasRuntimeAsset && !FrameworkUtilities.IsGenerationMoniker(validateFramework.Framework))
                            {
                                Log.LogError($"{ContractName} should be supported on {target} but has no runtime assets.");
                            }
                            else
                            {
                                var implementationAssemblies = runtimeAssetPaths.Where(IsDll);

                                Dictionary<string, string> implementationFiles = new Dictionary<string, string>();
                                foreach (var implementationAssembly in implementationAssemblies)
                                {
                                    var packageItem = _targetPathToPackageItem[implementationAssembly];
                                    Version implementationVersion = packageItem.Version;

                                    if (!VersionUtility.IsCompatibleApiVersion(supportedVersion, implementationVersion))
                                    {
                                        Log.LogError($"{ContractName} should support API version {supportedVersion} on {target} but {implementationAssembly} was found to support {implementationVersion?.ToString() ?? "<unknown version>"}.");
                                    }

                                    if (referenceAssemblyVersion != null &&
                                        HasSuppression(Suppression.PermitHigherCompatibleImplementationVersion) ? 
                                            !VersionUtility.IsCompatibleApiVersion(referenceAssemblyVersion, implementationVersion) :
                                            (implementationVersion != referenceAssemblyVersion))
                                    {
                                        Log.LogError($"{ContractName} has mismatched compile ({referenceAssemblyVersion}) and runtime ({implementationVersion}) versions on {target}.");
                                    }

                                    if (fx.Framework == FrameworkConstants.FrameworkIdentifiers.Net &&
                                        !referenceAssemblyVersion.Equals(implementationVersion))
                                    {
                                        Log.LogError($"{ContractName} has a higher runtime version ({implementationVersion}) than compile version ({referenceAssemblyVersion}) on .NET Desktop framework {target}.  This will break bindingRedirects.");
                                    }

                                    string fileName = Path.GetFileName(implementationAssembly);
                                    
                                    if (implementationFiles.ContainsKey(fileName))
                                    {
                                        Log.LogError($"{ContractName} includes both {implementationAssembly} and {implementationFiles[fileName]} an on {target} which have the same name and will clash when both packages are used.");
                                    }
                                    else
                                    {
                                        implementationFiles[fileName] = implementationAssembly;
                                    }

                                    if (packageItem.TargetFramework != fx && !runtimeFxSuppression.Contains(fx.ToString()))
                                    {
                                        // the selected asset wasn't an exact framework match, let's see if we have an exact match in any other runtime asset.                                        
                                        var matchingFxAssets = _targetPathToPackageItem.Values.Where(i => i.TargetFramework == fx &&  // exact framework
                                                                                                         // Same file
                                                                                                         Path.GetFileName(i.TargetPath).Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                                                                                                         // Is implementation
                                                                                                         (i.TargetPath.StartsWith("lib") || i.TargetPath.StartsWith("runtimes")) &&
                                                                                                         // is not the same source file as was already selected
                                                                                                         i.SourcePath != packageItem.SourcePath);

                                        if (matchingFxAssets.Any())
                                        {
                                            Log.LogError($"When targeting {target} {ContractName} will use {implementationAssembly} which targets {packageItem.TargetFramework.GetShortFolderName()}  but {String.Join(";", matchingFxAssets.Select(i => i.TargetPath))} targets {fx.GetShortFolderName()} specifically.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Set output items
            AllSupportedFrameworks = _frameworks.Values.Where(fx => fx.SupportedVersion != null).Select(fx => fx.ToItem()).OrderBy(i => i.ItemSpec).ToArray();

            if (!String.IsNullOrEmpty(ValidationReport))
            {
                report.Save(ValidationReport);
            }
        }

        private static bool IsDll(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private HashSet<string> GetSuppressionValues(Suppression key)
        {
            HashSet<string> values;
            _suppressions.TryGetValue(key, out values);
            return values;
        }

        private string GetSingleSuppressionValue(Suppression key)
        {
            var values = GetSuppressionValues(key);
            return (values != null && values.Count == 1) ? values.Single() : null;
        }


        private bool HasSuppression(Suppression key)
        {
            return _suppressions.ContainsKey(key);
        }

        private bool HasSuppression(Suppression key, string value)
        {
            HashSet<string> values;
            if (_suppressions.TryGetValue(key, out values) && values != null)
            {
                return values.Contains(value);
            }
            return false;
        }

        private void ExamineAssets(string assetType, string package, string target, IEnumerable<string> runtimeItems, out bool hasRealAsset, out bool hasPlaceHolder)
        {
            hasPlaceHolder = false;
            hasRealAsset = false;
            StringBuilder assetLog = new StringBuilder($"{assetType} assets for {ContractName} on {target}: ");
            if (runtimeItems != null && runtimeItems.Any())
            {
                foreach (var runtimeItem in runtimeItems)
                {
                    assetLog.AppendLine();
                    assetLog.Append($"  {runtimeItem}");

                    if (!hasRealAsset && NuGetAssetResolver.IsPlaceholder(runtimeItem))
                    {
                        hasPlaceHolder = true;
                    }
                    else
                    {
                        hasRealAsset = true;
                        hasPlaceHolder = false;
                    }
                }
            }
            else
            {
                assetLog.AppendLine();
                assetLog.Append("  <none>");
            }
            Log.LogMessage(LogImportance.Low, assetLog.ToString());
        }

        private void LoadSuppressions()
        {
            _suppressions = new Dictionary<Suppression, HashSet<string>>();
            if (File.Exists(SuppressionFile))
            {
                foreach (string suppression in File.ReadAllLines(SuppressionFile))
                {
                    if (suppression.TrimStart().StartsWith(@"//", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(suppression))
                    {
                        continue;
                    }

                    var parts = suppression.Split(new[] { '=' }, 2);
                    string keyString = null;
                    Suppression key;
                    HashSet<string> values = null;

                    if (parts.Length != 2)
                    {
                        // assume entire line is key
                        keyString = suppression;
                    }
                    else
                    {
                        keyString = parts[0];
                        values = new HashSet<string>(parts[1].Split(';'));
                    }

                    if (Enum.TryParse<Suppression>(keyString, out key))
                    {
                        _suppressions[key] = values;
                    }
                    else
                    {
                        Log.LogError($"{SuppressionFile} contained unkown suppression {keyString}");
                    }
                }
            }
        }

        private void LoadFiles()
        {
            _validateFiles = new Dictionary<string, List<PackageItem>>();
            foreach (var file in Files)
            {
                try
                {
                    var validateFile = new PackageItem(file);

                    if (String.IsNullOrWhiteSpace(validateFile.TargetPath))
                    {
                        Log.LogError($"{validateFile.TargetPath} is missing TargetPath metadata");
                    }

                    if (IsDll(validateFile.SourcePath))
                    {
                        if (validateFile.TargetFramework == null)
                        {
                            Log.LogError($"{validateFile.SourcePath} is missing TargetFramework metadata");
                        }
                        else if (validateFile.TargetPath.IndexOf(validateFile.TargetFramework.GetShortFolderName(), StringComparison.OrdinalIgnoreCase) == -1)
                        {
                            Log.LogError($"{validateFile.SourcePath} specifies TargetFramework {validateFile.TargetFramework} but TargetPath {validateFile.TargetPath} is missing the {validateFile.TargetFramework.GetShortFolderName()} qualifier");
                        }
                    }

                    if (!_validateFiles.ContainsKey(validateFile.Package))
                    {
                        _validateFiles[validateFile.Package] = new List<PackageItem>();
                    }
                    _validateFiles[validateFile.Package].Add(validateFile);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Could not parse File {file.ItemSpec}. {ex}");
                    // skip it.
                }
            }

            // build a map to translate back to source file from resolved asset
            // we use package-specific paths since we're resolving a set of packages.
            _targetPathToPackageItem = new Dictionary<string, PackageItem>();
            foreach (var packageFiles in _validateFiles)
            {
                foreach (PackageItem validateFile in packageFiles.Value)
                {
                    string packageSpecificTargetPath = AggregateNuGetAssetResolver.AsPackageSpecificTargetPath(packageFiles.Key, validateFile.TargetPath);

                    if (_targetPathToPackageItem.ContainsKey(packageSpecificTargetPath))
                    {
                        Log.LogError($"Files {_targetPathToPackageItem[packageSpecificTargetPath].SourcePath} and {validateFile.SourcePath} have the same TargetPath {packageSpecificTargetPath}.");
                    }
                    _targetPathToPackageItem[packageSpecificTargetPath] = validateFile;
                }
            }

            _resolver = new AggregateNuGetAssetResolver(RuntimeFile);
            foreach (string packageId in _validateFiles.Keys)
            {
                _resolver.AddPackageItems(packageId, _validateFiles[packageId].Select(f => f.TargetPath));
            }
        }

        private void LogPackageContent()
        {
            foreach (var packageId in _validateFiles.Keys)
            {
                Log.LogMessage(LogImportance.Low, $"Package {packageId}");
                foreach (var targetPath in _validateFiles[packageId].Select(pi => pi.TargetPath))
                {
                    Log.LogMessage(LogImportance.Low, $"  {targetPath}");
                }
            }
        }

        private void LoadSupport()
        {
            _frameworks = new Dictionary<NuGetFramework, ValidationFramework>();

            // determine which TxM:RIDs should be considered for support based on Frameworks item
            foreach (var framework in Frameworks)
            {
                NuGetFramework fx;
                try
                {
                    fx = FrameworkUtilities.ParseNormalized(framework.ItemSpec);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Could not parse Framework {framework.ItemSpec}. {ex}");
                    continue;
                }

                if (fx.Equals(NuGetFramework.UnsupportedFramework))
                {
                    Log.LogError($"Did not recognize {framework.ItemSpec} as valid Framework.");
                    continue;
                }

                string runtimeIdList = framework.GetMetadata("RuntimeIDs");
                
                if (_frameworks.ContainsKey(fx))
                {
                    Log.LogError($"Framework {fx} has been listed in Frameworks more than once.");
                    continue;
                }

                _frameworks[fx] = new ValidationFramework(fx);

                if (!String.IsNullOrWhiteSpace(runtimeIdList))
                {
                    _frameworks[fx].RuntimeIds = runtimeIdList.Split(';');
                }
            }

            // keep a list of explicitly listed supported frameworks so that we can check for conflicts.
            HashSet<NuGetFramework> explicitlySupportedFrameworks = new HashSet<NuGetFramework>();

            // determine what version should be supported based on SupportedFramework items
            foreach (var supportedFramework in SupportedFrameworks)
            {
                NuGetFramework fx;
                string fxString = supportedFramework.ItemSpec;
                bool isExclusiveVersion = fxString.Length > 1 && fxString[0] == '[' && fxString[fxString.Length - 1] == ']';
                if (isExclusiveVersion)
                {
                    fxString = fxString.Substring(1, fxString.Length - 2);
                }

                try
                {
                    fx = FrameworkUtilities.ParseNormalized(fxString);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Could not parse TargetFramework {fxString} as a SupportedFramework. {ex}");
                    continue;
                }

                if (fx.Equals(NuGetFramework.UnsupportedFramework))
                {
                    Log.LogError($"Did not recognize TargetFramework {fxString} as a SupportedFramework.");
                    continue;
                }

                Version supportedVersion;
                string version = supportedFramework.GetMetadata("Version");
                try
                {
                    supportedVersion = Version.Parse(version);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Could not parse Version {version} on SupportedFramework item {supportedFramework.ItemSpec}. {ex}");
                    continue;
                }

                ValidationFramework validationFramework = null;
                if (!_frameworks.TryGetValue(fx, out validationFramework))
                {
                    Log.LogError($"SupportedFramework {fx} was specified but is not part of the Framework list to use for validation.");
                    continue;
                }


                if (explicitlySupportedFrameworks.Contains(fx))
                {
                    if (validationFramework.SupportedVersion != supportedVersion)
                    {
                        Log.LogError($"Framework {fx} has been listed in SupportedFrameworks more than once with different versions {validationFramework.SupportedVersion} and {supportedVersion}.  Framework should only be listed once with the expected API version for that platform.");
                    }
                    continue;
                }
                explicitlySupportedFrameworks.Add(fx);

                validationFramework.SupportedVersion = supportedVersion;
                
                if (!isExclusiveVersion)
                {
                    // find all frameworks of higher version, sorted by version ascending
                    var higherFrameworks = _frameworks.Values.Where(vf => vf.Framework.Framework == fx.Framework && vf.Framework.Version > fx.Version).OrderBy(vf => vf.Framework.Version);

                    foreach(var higherFramework in higherFrameworks)
                    {
                        if (higherFramework.SupportedVersion != null && higherFramework.SupportedVersion > supportedVersion)
                        {
                            // found an higher framework version a higher API version, stop applying this supported version
                            break;
                        }

                        higherFramework.SupportedVersion = supportedVersion;
                    }
                }
            }


            // determine which Frameworks should support inbox
            _frameworkSet = FrameworkSet.Load(FrameworkListsPath);
            foreach (IEnumerable<Framework> inboxFxGroup in _frameworkSet.Frameworks.Values)
            {
                foreach (Framework inboxFx in inboxFxGroup)
                {
                    // get currently supported version to see if we have OOB'ed it
                    Version inboxVersion = null;
                    inboxFx.Assemblies.TryGetValue(ContractName, out inboxVersion);

                    if (inboxVersion != null)
                    {
                        NuGetFramework fx = FrameworkUtilities.ParseNormalized(inboxFx.ShortName);
                        ValidationFramework validationFramework = null;
                        if (_frameworks.TryGetValue(fx, out validationFramework))
                        {
                            Version supportedVersion = validationFramework.SupportedVersion;

                            if (supportedVersion != null &&
                                (supportedVersion.Major > inboxVersion.Major ||
                                (supportedVersion.Major == inboxVersion.Major && supportedVersion.Minor > inboxVersion.Minor)))
                            {
                                // Higher major.minor
                                Log.LogMessage(LogImportance.Low, $"Framework {fx} supported {ContractName} as inbox but the current supported version {supportedVersion} is higher in major.minor than inbox version {inboxVersion}.  Assuming out of box.");
                                continue;
                            }
                            else if (supportedVersion != null && supportedVersion < inboxVersion && inboxVersion != s_maxVersion)
                            {
                                // Lower version
                                Log.LogError($"Framework {fx} supports {ContractName} as inbox but the current supported version {supportedVersion} is lower than the inbox version {inboxVersion}");
                            }

                            // equal major.minor, build.revision difference is permitted, prefer the version listed by ContractSupport item
                        }

                        if (validationFramework == null)
                        {
                            // we may not be explicitly validating for this framework so add it to validate inbox assets.
                            _frameworks[fx] = validationFramework = new ValidationFramework(fx)
                            {
                                SupportedVersion = inboxVersion
                            };
                        }

                        validationFramework.IsInbox = true;
                    }
                }
            }

            // for every framework we know about, also infer it's netstandard version to ensure it can
            // be targeted by PCL.  Even if a package only supports a single framework we still
            // want to include a portable reference assembly.  This allows 3rd parties to add 
            // their own implementation via a lineup/runtime.json.

            // only consider frameworks that support the contract at a specific version
            var inferFrameworks = _frameworks.Values.Where(fx => fx.SupportedVersion != null && fx.SupportedVersion != s_maxVersion).ToArray();

            var genVersionSuppression = GetSuppressionValues(Suppression.PermitPortableVersionMismatch) ?? new HashSet<string>();
            var inferNETStandardSuppression = GetSuppressionValues(Suppression.SuppressNETStandardInference) ?? new HashSet<string>();
            Dictionary<NuGetFramework, ValidationFramework> generationsToValidate = new Dictionary<NuGetFramework, ValidationFramework>();
            foreach (var inferFramework in inferFrameworks)
            {
                var inferFrameworkMoniker = inferFramework.Framework.ToString();
                if (inferNETStandardSuppression.Contains(inferFrameworkMoniker))
                {
                    continue;
                }

                NuGetFramework generation = new NuGetFramework(_generationIdentifier, Generations.DetermineGenerationForFramework(inferFramework.Framework, UseNetPlatform));
                Log.LogMessage(LogImportance.Low, $"Validating {generation} for {ContractName}, {inferFramework.SupportedVersion} since it is supported by {inferFrameworkMoniker}");

                ValidationFramework existingGeneration = null;
                if (generationsToValidate.TryGetValue(generation, out existingGeneration))
                {
                    // the netstandard version should be the minimum version supported by all platforms that support that netstandard version.
                    if (inferFramework.SupportedVersion < existingGeneration.SupportedVersion)
                    {
                        Log.LogMessage($"Framework {inferFramework.Framework} supports {ContractName} at {inferFramework.SupportedVersion} which is lower than {existingGeneration.SupportedVersion} supported by generation {generation.GetShortFolderName()}.  Lowering the version supported by {generation.GetShortFolderName()}.");
                        existingGeneration.SupportedVersion = inferFramework.SupportedVersion;
                    }
                }
                else
                {
                    generationsToValidate.Add(generation, new ValidationFramework(generation) { SupportedVersion = inferFramework.SupportedVersion });
                }
            }

            foreach (var generation in generationsToValidate)
            {
                _frameworks.Add(generation.Key, generation.Value);
            }

            // for every generation supported explcitly in implementation, ensure
            // it can be targeted by PCL.
            foreach (var packageGroup in _resolver.GetAllRuntimeItems())
            {
                var allGenerationalImplementations = packageGroup.Value
                    .Where(contentGroup => FrameworkUtilities.IsGenerationMoniker(contentGroup.Properties[PropertyNames.TargetFrameworkMoniker] as NuGetFramework))
                    .SelectMany(contentGroup => contentGroup.Items.Select(item => _targetPathToPackageItem[AggregateNuGetAssetResolver.AsPackageSpecificTargetPath(packageGroup.Key, item.Path)]));

                foreach (var generationalImplementation in allGenerationalImplementations)
                {
                    NuGetFramework generation = generationalImplementation.TargetFramework;
                    if (_frameworks.ContainsKey(generation))
                    {
                        continue;
                    }

                    Version supportedVersion = generationalImplementation.Version;

                    Log.LogMessage(LogImportance.Low, $"Validating {generation} for {ContractName}, {supportedVersion} since it is supported by {generationalImplementation.TargetPath}");

                    _frameworks.Add(generation, new ValidationFramework(generation) { SupportedVersion = supportedVersion });
                }
            }
        }

        private ValidationReport CreateValidationReport()
        {
            return new ValidationReport()
            {
                Id = PackageId,
                Version = PackageVersion,
                SupportedFrameworks = _frameworks.OrderBy(fx => fx.Key.ToString())
                                                .Where(fx => fx.Value.SupportedVersion != null)
                                                .ToDictionary(fx => fx.Key.ToString(), fx => GetVersionString(fx.Value.SupportedVersion))
            };

        }

        private PackageAsset GetPackageAssetFromTargetPath(string targetPath)
        {
            PackageItem packageItem = null;
            if (!_targetPathToPackageItem.TryGetValue(targetPath, out packageItem))
            {
                throw new ArgumentException($"Could not find source item for {targetPath}", nameof(targetPath));
            }

            return new PackageAsset()
            {
                LocalPath = packageItem.SourcePath,
                SourceProject = packageItem.SourceProject,
                PackagePath = packageItem.TargetPath
            };
        }

        private string GetVersionString(Version version)
        {
            // normalize to API version
            return version == s_maxVersion ? "Any" : _frameworkSet.GetApiVersion(ContractName, version)?.ToString();
        }

        private class ValidationFramework
        {
            private static readonly string[] s_nullRidList = new string[] { null };
            public ValidationFramework(NuGetFramework framework)
            {
                Framework = framework;
                RuntimeIds = s_nullRidList;
            }

            public NuGetFramework Framework { get; }
            public string[] RuntimeIds { get; set; }

            // if null indicates the contract should not be supported.
            public Version SupportedVersion { get; set; }
            public bool IsInbox { get; set; }
            public string ShortName { get { return Framework.GetShortFolderName(); } }

            public ITaskItem ToItem()
            {
                ITaskItem item = new TaskItem(Framework.ToString());
                item.SetMetadata("ShortName", ShortName);
                item.SetMetadata("Version", SupportedVersion.ToString());
                item.SetMetadata("Inbox", IsInbox.ToString());
                item.SetMetadata("ValidatedRIDs", String.Join(";", RuntimeIds));
                return item;
            }
        }
    }
    public enum Suppression
    {
        /// <summary>
        /// Permits a runtime asset of the targets specified, semicolon delimited
        /// </summary>
        PermitImplementation,
        /// <summary>
        /// Permits a lower version on specified frameworks, semicolon delimitied, than the generation supported by that framework
        /// </summary>
        PermitPortableVersionMismatch,
        /// <summary>
        /// Permits a compatible API version match between ref and impl, rather than exact match
        /// </summary>
        PermitHigherCompatibleImplementationVersion,
        /// <summary>
        /// Permits a non-matching targetFramework asset to be used even when a matching one exists.
        /// </summary>
        PermitRuntimeTargetMonikerMismatch,
        /// <summary>
        /// Suppresses a particular set of SupportedFrameworks from inferring NETStandard support.
        /// EG: package supports netcore45, wp8, net451, wpa81.
        ///     package cannot support net45, and thus doesn't wish to expose netstandard1.0 or netstandard1.1
        ///     reference assemblies.
        ///     It can use SuppressNETStandardInference=WindowsPhone,Version=v8.0;.NETCore,Version=v4.5 to still 
        ///     validate support for wp8 and netcore45 without forcing it to support netstandard1.0 and 1.1.
        /// </summary>
        SuppressNETStandardInference
    }
}
