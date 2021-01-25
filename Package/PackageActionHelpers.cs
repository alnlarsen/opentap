﻿//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using OpenTap.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenTap.Package
{
    internal static class PackageActionHelpers
    {
        readonly static TraceSource log =  OpenTap.Log.CreateSource("PackageAction");

        private enum DepResponse
        {
            Add,
            Ignore
        }

        [System.Reflection.Obfuscation(Exclude = true)]
        private class DepRequest
        {
            [Browsable(true)]
            [Layout(LayoutMode.FullRow)]
            public string Message => message;
            internal string message;
            internal string PackageName { get; set; }
            [Submit]
            public DepResponse Response { get; set; } = DepResponse.Add;
        }

        internal static PackageDef FindPackage(PackageSpecifier packageReference, IEnumerable<IPackageIdentifier> compatibleWith, List<IPackageRepository> repositories)
        {
            var compatiblePackages = PackageRepositoryHelpers.GetPackagesFromAllRepos(repositories, packageReference, compatibleWith.ToArray());

            // Of the compatible packages, pick the one with the highest version number. If that package is available from several repositories, pick the one with the lowest index in the list in PackageManagerSettings
            PackageDef package = null;
            if (compatiblePackages.Any())
                package = compatiblePackages.GroupBy(p => p.Version).OrderByDescending(g => g.Key).FirstOrDefault()
                                            .OrderBy(p => repositories.IndexWhen(e => NormalizeRepoUrl(e.Url) == NormalizeRepoUrl((p.PackageSource as IRepositoryPackageDefSource)?.RepositoryUrl)))
                                            .OrderBy(p => OrderArchitecture(p.Architecture))
                                            .FirstOrDefault();

            // If no package was found, try to figure out why
            if (package == null)
            {
                var compatibleVersions = PackageRepositoryHelpers.GetAllVersionsFromAllRepos(repositories, packageReference.Name, compatibleWith.ToArray());
                var versions = PackageRepositoryHelpers.GetAllVersionsFromAllRepos(repositories, packageReference.Name);

                // Any packages compatible with opentap and platform
                var filteredVersions = compatibleVersions.Where(v => v.IsPlatformCompatible(packageReference.Architecture, packageReference.OS)).ToList();
                if (filteredVersions.Any())
                {
                    // if the specified version exist, don't say it could not be found. 
                    if (versions.Any(v => packageReference.Version.IsCompatible(v.Version)))
                        throw new ExitCodeException(1, $"Package '{packageReference.Name}' matching version '{packageReference.Version}' is not compatible. Latest compatible version is '{filteredVersions.FirstOrDefault().Version}'.");
                    else
                        throw new ExitCodeException(1, $"Package '{packageReference.Name}' matching version '{packageReference.Version}' could not be found. Latest compatible version is '{filteredVersions.FirstOrDefault().Version}'.");
                }

                // Any compatible with platform but not opentap
                filteredVersions = versions.Where(v => v.IsPlatformCompatible(packageReference.Architecture, packageReference.OS)).ToList();
                if (filteredVersions.Any() && compatibleWith.Any())
                {
                    var opentapPackage = compatibleWith.First();
                    throw new ExitCodeException(1, $"Package '{packageReference.Name}' does not exist in a version compatible with '{opentapPackage.Name}' version '{opentapPackage.Version}'.");
                }

                // Any compatible with opentap but not platform
                if (compatibleVersions.Any())
                {
                    if (packageReference.Version != VersionSpecifier.Any || packageReference.OS != null || packageReference.Architecture != CpuArchitecture.Unspecified)
                        throw new ExitCodeException(1,
                            string.Format("No '{0}' package {1} was found.", packageReference.Name, string.Join(" and ",
                                new string[] {
                                    packageReference.Version != VersionSpecifier.Any ? $"compatible with version '{packageReference.Version}'": null,
                                    packageReference.OS != null ? $"compatible with '{packageReference.OS}' operating system" : null,
                                    packageReference.Architecture != CpuArchitecture.Unspecified ? $"with '{packageReference.Architecture}' architecture" : null
                            }.Where(x => x != null).ToArray())));
                    else
                        throw new ExitCodeException(1, $"Package '{packageReference.Name}' does not exist in a version compatible with this OS and architecture.");
                }

                // Any version
                if (versions.Any())
                {
                    var opentapPackage = compatibleWith.FirstOrDefault();
                    if (opentapPackage != null)
                        throw new ExitCodeException(1, $"Package '{packageReference.Name}' does not exist in a version compatible with this OS, architecture and '{opentapPackage.Name}' version '{opentapPackage.Version}'.");
                    else
                        throw new ExitCodeException(1, $"Package '{packageReference.Name}' does not exist in a version compatible with this OS and architecture.");
                }

                throw new ExitCodeException(1, $"Package '{packageReference.Name}' could not be found in any repository.");
            }
            return package;
        }

        private static int OrderArchitecture(CpuArchitecture architecture)
        {
            if (architecture == ArchitectureHelper.GuessBaseArchitecture)
                return 0;
            switch (architecture)
            {
                case CpuArchitecture.Unspecified:
                    return 10;
                case CpuArchitecture.AnyCPU:
                    return 1;
                case CpuArchitecture.x86:
                    return 3;
                case CpuArchitecture.x64:
                    return 2;
                case CpuArchitecture.arm:
                    return 5;
                case CpuArchitecture.arm64:
                    return 4;
                default:
                    return 10;
            }
        }

        internal static string NormalizeRepoUrl(string path)
        {
            if (Uri.IsWellFormedUriString(path, UriKind.Relative) && Directory.Exists(path) || Regex.IsMatch(path ?? "", @"^([A-Z|a-z]:)?(\\|/)"))
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    return Path.GetFullPath(path)
                               .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .ToUpperInvariant();
                else
                    return Path.GetFullPath(path)
                               .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            else if(path.StartsWith("http"))
                return path.ToUpperInvariant();
            else
                return String.Format("http://{0}",path).ToUpperInvariant();

        }

        internal static List<PackageDef> GatherPackagesAndDependencyDefs(Installation installation, PackageSpecifier[] pkgRefs, string[] packageNames, string Version, CpuArchitecture arch, string OS, List<IPackageRepository> repositories, 
            bool force, bool includeDependencies, bool ignoreDependencies, bool askToIncludeDependencies, bool noDowngrade)
        {
            List<PackageDef> gatheredPackages = new List<PackageDef>();

            List<PackageSpecifier> packages = new List<PackageSpecifier>();
            if (pkgRefs != null)
                packages = pkgRefs.ToList();
            else
            {
                if (packageNames == null)
                    throw new Exception("No packages specified.");
                foreach (string packageName in packageNames)
                {
                    var version = Version;
                    if (Path.GetExtension(packageName).ToLower().EndsWith("tappackages"))
                    {
                        var tempDir = Path.GetTempPath();
                        var bundleFiles = PluginInstaller.UnpackPackage(packageName, tempDir);
                        var packagesInBundle = bundleFiles.Select(PackageDef.FromPackage);

                        // A packages file may contain the several variants of the same package, try to select one based on OS and Architecture
                        foreach (IGrouping<string, PackageDef> grp in packagesInBundle.GroupBy(p => p.Name))
                        {
                            var selected = grp.ToList();
                            if (selected.Count == 1)
                            {
                                gatheredPackages.Add(selected.First());
                                continue;
                            }
                            if (!string.IsNullOrEmpty(OS))
                            {
                                selected = selected.Where(p => p.OS.ToLower().Split(',').Any(OS.ToLower().Contains)).ToList();
                                if (selected.Count == 1)
                                {
                                    gatheredPackages.Add(selected.First());
                                    log.Debug("TapPackages file contains packages for several operating systems. Picking only the one for {0}.", OS);
                                    continue;
                                }
                            }
                            if (arch != CpuArchitecture.Unspecified)
                            {
                                selected = selected.Where(p => ArchitectureHelper.CompatibleWith(arch, p.Architecture)).ToList();
                                if (selected.Count == 1)
                                {
                                    gatheredPackages.Add(selected.First());
                                    log.Debug("TapPackages file contains packages for several CPU architectures. Picking only the one for {0}.", arch);
                                    continue;
                                }
                            }
                            throw new Exception("TapPackages file contains multiple variants of the same package. Unable to autoselect a suitable one.");
                        }
                    }
                    else if (string.IsNullOrWhiteSpace(packageName) == false)
                    {
                        packages.Add(new PackageSpecifier(packageName, VersionSpecifier.Parse(version ?? ""), arch, OS));
                    }
                }
            }

            foreach (var packageReference in packages)
            {
                var installedPackages = installation.GetPackages();
                Stopwatch timer = Stopwatch.StartNew();
                if (File.Exists(packageReference.Name))
                {
                    var package = PackageDef.FromPackage(packageReference.Name);

                    if (noDowngrade)
                    {
                        var installedPackage = installedPackages.FirstOrDefault(p => p.Name == package.Name);
                        if (installedPackage != null && installedPackage.Version.CompareTo(package.Version) >= 0)
                        {
                            log.Info($"The same or a newer version of package '{package.Name}' in already installed.");
                            continue;
                        }
                    }
                    
                    gatheredPackages.Add(package);
                    log.Debug(timer, "Found package {0} locally.", packageReference.Name);
                }
                else
                {
                    var compatibleWith = force ? new List<PackageDef>() : gatheredPackages.Concat(installedPackages);
                    PackageDef package = FindPackage(packageReference, compatibleWith, repositories);
                    
                    if (noDowngrade)
                    {
                        var installedPackage = installedPackages.FirstOrDefault(p => p.Name == package.Name);
                        if (installedPackage != null && installedPackage.Version.CompareTo(package.Version) >= 0)
                        {
                            log.Info($"The same or a newer version of package '{package.Name}' in already installed.");
                            continue;
                        }
                    }

                    if (PackageCacheHelper.PackageIsFromCache(package))
                        log.Debug(timer, "Found package {0} version {1} in local cache", package.Name, package.Version);
                    else
                        log.Debug(timer, "Found package {0} version {1}", package.Name, package.Version);

                    gatheredPackages.Add(package);
                }
            }

            if (gatheredPackages.All(p => p.IsBundle()))
            {
                // If we are just installing bundles, we can assume that dependencies should also be installed
                includeDependencies = true;
            }
            
            log.Debug("Resolving dependencies.");
            var resolver = new DependencyResolver(installation, gatheredPackages, repositories);
            if(ignoreDependencies)
            {
                if(resolver.UnknownDependencies.Any() || resolver.MissingDependencies.Any())
                    log.Info($"Ignoring depencencies (--no-dependencies option specified).");
            }
            if (force)
            {
                // this it for compatibility with old 9.11 behavior (--force means don't ask for dependencies).
                if (resolver.UnknownDependencies.Any() || resolver.MissingDependencies.Any())
                    log.Info($"Ignoring depencencies (--force option specified).");
            }
            else if (resolver.UnknownDependencies.Any())
            {
                foreach (var dep in resolver.UnknownDependencies)
                {
                    string message = string.Format("A package dependency named '{0}' with a version compatible with {1} could not be found in any repository.", dep.Name, dep.Version);

                    if (force)
                    {
                        log.Warning(message);
                        log.Warning("Continuing without downloading dependencies. Plugins will likely not work as expected.", dep.Name);
                    }
                    else
                        log.Error(message);
                }
                if (!force)
                {
                    log.Info("To download package dependencies despite the conflicts, use the --force option.");
                    return null;
                }
            }
            else if (resolver.MissingDependencies.Any())
            {
                if (includeDependencies == false)
                {
                    var dependencies = string.Join(", ",
                        resolver.MissingDependencies.Select(d => $"{d.Name} {d.Version}"));
                    log.Info($"Use '--dependencies' to include {dependencies}.");
                }

                if (includeDependencies)
                {
                    foreach (var package in resolver.MissingDependencies)
                    {
                        log.Debug($"Adding dependency {package.Name} {package.Version}");
                        gatheredPackages.Insert(0, package);
                    }
                }
                else if (askToIncludeDependencies)
                {
                    var pkgs = new List<DepRequest>();

                    foreach (var package in resolver.MissingDependencies)
                    {
                        // Handle each package at a time.
                        DepRequest req = null;
                        pkgs.Add(req = new DepRequest { PackageName = package.Name, message = string.Format("Add dependency {0} {1} ?", package.Name, package.Version), Response = DepResponse.Add });
                        UserInput.Request(req, true);
                    }

                    foreach (var pkg in resolver.MissingDependencies)
                    {
                        var res = pkgs.FirstOrDefault(r => r.PackageName == pkg.Name);

                        if ((res != null) && res.Response == DepResponse.Add)
                        {
                            gatheredPackages.Insert(0, pkg);
                        }
                        else
                            log.Debug("Ignoring dependent package {0} at users request.", pkg.Name);
                    }
                }
            }

            return gatheredPackages;
        }

        internal static List<string> DownloadPackages(string destinationDir, List<PackageDef> PackagesToDownload, List<string> filenames = null)
        {
            List<string> downloadedPackages = new List<string>();
            for(int i = 0; i < PackagesToDownload.Count; i++)
            {
                Stopwatch timer = Stopwatch.StartNew();
                
                var pkg = PackagesToDownload[i]; 
                string filename = filenames?.ElementAtOrDefault(i) ?? Path.Combine(destinationDir, GetQualifiedFileName(pkg));

                TapThread.ThrowIfAborted();
                
                try
                {
                    PackageDef existingPkg = null;
                    try
                    {
                        // If the package we are installing is from a file, we should always use that file instead of a cached package.
                        // During development a package might not change version but still have different content.
                        if (pkg.PackageSource is FilePackageDefSource == false && File.Exists(filename))
                            existingPkg = PackageDef.FromPackage(filename);
                    }
                    catch (Exception e)
                    {
                        log.Warning("Could not open OpenTAP Package. Redownloading package.", e);
                        File.Delete(filename);
                    }
                    
                    if (existingPkg != null)
                    {
                        if (existingPkg.Version == pkg.Version && existingPkg.OS == pkg.OS && existingPkg.Architecture == pkg.Architecture)
                        {
                            if(!PackageCacheHelper.PackageIsFromCache(existingPkg))
                                log.Info("Package '{0}' already exists in '{1}'.", pkg.Name, destinationDir);
                            else
                                log.Info("Package '{0}' already exists in cache '{1}'.", pkg.Name, destinationDir);
                        }
                        else
                        {
                            throw new Exception($"A package already exists but it is not the same as the package that is being downloaded.");
                        }
                    }
                    else
                    {
                        string source = (pkg.PackageSource as IRepositoryPackageDefSource)?.RepositoryUrl;
                        if (source == null && pkg.PackageSource is FilePackageDefSource fileSource)
                            source = fileSource.PackageFilePath;
                        
                        IPackageRepository rm = PackageRepositoryHelpers.DetermineRepositoryType(source);
                        if (PackageCacheHelper.PackageIsFromCache(pkg))
                        {
                            rm.DownloadPackage(pkg, filename);
                            log.Info(timer, "Found package '{0}' in cache. Copied to '{1}'.", pkg.Name, Path.GetFullPath(filename));
                        }
                        else
                        {
                            log.Debug("Downloading '{0}' version '{1}' from '{2}'", pkg.Name, pkg.Version, source);
                            rm.DownloadPackage(pkg, filename);
                            log.Info(timer, "Downloaded '{0}' to '{1}'.", pkg.Name, Path.GetFullPath(filename));
                            PackageCacheHelper.CachePackage(filename);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Failed to download OpenTAP package.");
                    throw ex;
                }

                downloadedPackages.Add(filename);
            }

            return downloadedPackages;
        }

        internal static string GetQualifiedFileName(PackageDef pkg)
        {
            List<string> filenameParts = new List<string> { pkg.Name };
            if (pkg.Version != null)
                filenameParts.Add(pkg.Version.ToString());
            if (pkg.Architecture != CpuArchitecture.AnyCPU)
                filenameParts.Add(pkg.Architecture.ToString());
            if (!String.IsNullOrEmpty(pkg.OS) && pkg.OS != "Windows")
                filenameParts.Add(pkg.OS);
            filenameParts.Add("TapPackage");
            return String.Join(".", filenameParts);
        }
    }
}
