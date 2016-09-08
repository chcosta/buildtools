﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoUtil
{
    internal static class Program
    {
        private sealed class ParsedArgs
        {
            internal string RepoDataPath { get; set; }
            internal string SourcesPath { get; set; }
            internal string[] RemainingArgs { get; set; }
        }

        private delegate ICommand CreateCommand(RepoConfig repoConfig, string sourcesPath);

        internal static int Main(string[] args)
        {
            Console.WriteLine("Attach a debugger now or press <Enter> to continue.");
            Console.ReadLine();
            int result = 1;
            try
            {
                if (Run(args))
                    result = 0;
            }
            catch (ConflictingPackagesException ex)
            {
                Console.WriteLine(ex.Message);
                foreach (var package in ex.ConflictingPackages.OrderBy(p => p.PackageName))
                {
                    Console.WriteLine(package.PackageName);
                    Console.WriteLine($"\t{package.Conflict.NuGetPackage.Version} - {package.Conflict.FileName}");
                    Console.WriteLine($"\t{package.Original.NuGetPackage.Version} - {package.Original.FileName}");
                }

                Console.WriteLine(GenerateMyConflicts(ex.ConflictingPackages));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Something unexpected happened.");
                Console.WriteLine(ex.ToString());
            }

            return result;
        }

        private static string GenerateMyConflicts(System.Collections.Immutable.ImmutableArray<NuGetPackageConflict> conflictingPackages)
        {
            StringBuilder returnString = new StringBuilder();
            Dictionary<string, List<string>> myConflicts = new Dictionary<string, List<string>>();

            var packages = conflictingPackages.GroupBy(p => p.PackageName).ToDictionary(p => p.Key, p => p.ToArray());
            Regex versionRegex = new Regex(@"\d+\.\d+\.\d+-.*");
            HashSet<string> prereleaseVersions = new HashSet<string>();
            foreach(var package in packages)
            {
                returnString.Append("\"" + package.Key + "\": [ ");
                var conflict = package.Value.Where(w => !versionRegex.IsMatch(w.Conflict.NuGetPackage.Version.ToString())).Select(s => "\"" + s.Conflict.NuGetPackage.Version.ToString() + "\"");
                var original = package.Value.Where(w => !versionRegex.IsMatch(w.Original.NuGetPackage.Version.ToString())).Select(s => "\"" + s.Original.NuGetPackage.Version.ToString() + "\"");
                var preRelConflict = package.Value.Where(w => versionRegex.IsMatch(w.Conflict.NuGetPackage.Version.ToString())).Select(s => s.Conflict.NuGetPackage.Version.ToString());
                var preRelOriginal = package.Value.Where(w => versionRegex.IsMatch(w.Original.NuGetPackage.Version.ToString())).Select(s => s.Original.NuGetPackage.Version.ToString());

                var distinct = conflict.Union(original);
                returnString.Append(string.Join(", ", distinct.Where(d => !versionRegex.IsMatch(d))));
                returnString.AppendLine(" ]");

                /* build up a list of prerelease versions to determine if there will be a conflict */
                foreach (var d in preRelConflict.Union(preRelOriginal).Where(w => versionRegex.IsMatch(w)))
                {
                    prereleaseVersions.Add(d);
                }
            }

            returnString.AppendLine(Environment.NewLine + "PreRelease versions");
            returnString.AppendLine(string.Join(", ", prereleaseVersions));
            return returnString.ToString();
        }

        private static bool Run(string[] args)
        {
            ParsedArgs parsedArgs;
            CreateCommand func;
            if (!TryParseCommandLine(args, out parsedArgs, out func))
            {
                return false;
            }

            RepoConfig repoConfig = null;
            if (!string.IsNullOrEmpty(parsedArgs.RepoDataPath))
                repoConfig = RepoConfig.ReadFrom(parsedArgs.RepoDataPath);
            var command = func(repoConfig, parsedArgs.SourcesPath);
            return command.Run(Console.Out, parsedArgs.RemainingArgs);
        }

        private static bool TryParseCommandLine(string[] args, out ParsedArgs parsedArgs, out CreateCommand func)
        {
            func = null;
            parsedArgs = new ParsedArgs();

            // Setup the default values
            parsedArgs.SourcesPath = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);

            var index = 0;
            if (!TryParseCommon(args, ref index, parsedArgs))
            {
                return false;
            }

            if (string.IsNullOrEmpty(parsedArgs.RepoDataPath))
                throw new ArgumentException("The -repoDataPath switch is required.");

            if (!TryParseCommand(args, ref index, out func))
            {
                return false;
            }

            parsedArgs.RemainingArgs = index >= args.Length
                ? Array.Empty<string>()
                : args.Skip(index).ToArray();
            return true;
        }

        private static bool TryParseCommon(string[] args, ref int index, ParsedArgs parsedArgs)
        {
            while (index < args.Length)
            {
                var arg = args[index];
                if (arg[0] != '-')
                {
                    return true;
                }

                index++;
                switch (arg.ToLower())
                {
                    case "-sourcespath":
                        {
                            if (index < args.Length)
                            {
                                parsedArgs.SourcesPath = args[index];
                                index++;
                            }
                            else
                            {
                                Console.WriteLine($"The -sourcesPath switch needs a value");
                                return false;
                            }
                            break;
                        }
                    case "-repodatapath":
                        {
                            if (index < args.Length)
                            {
                                parsedArgs.RepoDataPath = args[index];
                                index++;
                            }
                            else
                            {
                                Console.WriteLine($"The -repoDataPath switch needs a value");
                                return false;
                            }
                            break;
                        }
                    default:
                        Console.Write($"Option {arg} is unrecognized");
                        return false;
                }
            }

            return true;
        }

        private static bool TryParseCommand(string[] args, ref int index, out CreateCommand func)
        {
            func = null;

            if (index >= args.Length)
            {
                Console.WriteLine("Need a command to run");
                return false;
            }

            var name = args[index];
            switch (name)
            {
                case "verify":
                    func = (c, s) => new VerifyCommand(c, s);
                    break;
                case "view":
                    func = (c, s) => new ViewCommand(c, s);
                    break;
                case "consumes":
                    func = (c, s) => new ConsumesCommand(RepoData.Create(c, s));
                    break;
                case "change":
                    func = (c, s) => new ChangeCommand(RepoData.Create(c, s));
                    break;
                case "produces":
                    func = (c, s) => new ProducesCommand(c, s);
                    break;
                default:
                    Console.Write($"Command {name} is not recognized");
                    return false;
            }

            index++;
            return true;
        }
    }
}
