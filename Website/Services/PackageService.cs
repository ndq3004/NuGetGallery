﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Transactions;
using NuGet;

namespace NuGetGallery {
    public class PackageService : IPackageService {
        readonly ICryptographyService cryptoSvc;
        readonly IEntityRepository<PackageRegistration> packageRegistrationRepo;
        readonly IEntityRepository<Package> packageRepo;
        readonly IPackageFileService packageFileSvc;

        public PackageService(
            ICryptographyService cryptoSvc,
            IEntityRepository<PackageRegistration> packageRegistrationRepo,
            IEntityRepository<Package> packageRepo,
            IPackageFileService packageFileSvc) {
            this.cryptoSvc = cryptoSvc;
            this.packageRegistrationRepo = packageRegistrationRepo;
            this.packageRepo = packageRepo;
            this.packageFileSvc = packageFileSvc;
        }

        public Package CreatePackage(IPackage nugetPackage, User currentUser) {
            var packageRegistration = CreateOrGetPackageRegistration(currentUser, nugetPackage);

            var package = CreatePackageFromNuGetPackage(packageRegistration, nugetPackage);
            packageRegistration.Packages.Add(package);

            using (var tx = new TransactionScope())
            using (var stream = nugetPackage.GetStream()) {
                packageRegistrationRepo.CommitChanges();
                packageFileSvc.SavePackageFile(package, stream);
                tx.Complete();
            }

            return package;
        }

        public void DeletePackage(string id, string version) {
            var package = FindPackageByIdAndVersion(id, version);

            if (package == null)
                throw new EntityException(Strings.PackageWithIdAndVersionNotFound, id, version);

            using (var tx = new TransactionScope()) {
                var packageRegistration = package.PackageRegistration;
                packageRepo.DeleteOnCommit(package);
                packageFileSvc.DeletePackageFile(id, version);
                packageRepo.CommitChanges();
                if (packageRegistration.Packages.Count == 0) {
                    packageRegistrationRepo.DeleteOnCommit(packageRegistration);
                    packageRegistrationRepo.CommitChanges();
                }
                tx.Complete();
            }
        }

        public virtual PackageRegistration FindPackageRegistrationById(string id) {
            return packageRegistrationRepo.GetAll()
                .Include(pr => pr.Owners)
                .Where(pr => pr.Id == id)
                .SingleOrDefault();
        }

        public virtual Package FindPackageByIdAndVersion(string id, string version = null) {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException("id");

            // Optimization: Everytime we look at a package we almost always want to see 
            // all the other packages with the same via the PackageRegistration property. 
            // This resulted in a gnarly query. 
            // Instead, we can always query for all packages with the ID and then fix up 
            // the Packages property for the one we plan to return.
            var packageVersions = packageRepo.GetAll()
                    .Include(p => p.Authors)
                    .Include(p => p.Reviews)
                    .Include(p => p.PackageRegistration)
                    .Where(p => p.PackageRegistration.Id == id).ToList();

            Package package = null;
            if (version == null) {
                package = packageVersions
                    .Where(p => new Version(p.Version) == packageVersions.Max(p2 => new Version(p2.Version)))
                    .SingleOrDefault();
            }
            else {
                package = packageVersions
                    .Where(p => p.PackageRegistration.Id == id && p.Version == version)
                    .SingleOrDefault();
            }
            if (package != null) {
                package.PackageRegistration.Packages = packageVersions;
            }
            return package;
        }

        public IEnumerable<Package> GetLatestVersionOfPublishedPackages() {
            return packageRepo.GetAll()
                .Include(x => x.PackageRegistration)
                .Include(x => x.Authors)
                .Include(x => x.PackageRegistration.Owners)
                .Include(x => x.PackageRegistration.Packages)
                .Include(x => x.Reviews)
                .Where(pv => pv.Published != null && pv.IsLatest)
                .ToList();
        }

        public IEnumerable<Package> FindPackagesByOwner(User user) {
            return (from pr in packageRegistrationRepo.GetAll()
                    from u in pr.Owners
                    where u.Username == user.Username
                    from p in pr.Packages
                    select p).Include(p => p.PackageRegistration).ToList();
        }

        public void PublishPackage(string id, string version) {
            var package = FindPackageByIdAndVersion(id, version);

            if (package == null)
                throw new EntityException(Strings.PackageWithIdAndVersionNotFound, id, version);

            package.Published = DateTime.UtcNow;

            UpdateIsLatest(package.PackageRegistration);

            packageRepo.CommitChanges();
        }

        PackageRegistration CreateOrGetPackageRegistration(User currentUser, IPackage nugetPackage) {
            var packageRegistration = FindPackageRegistrationById(nugetPackage.Id);

            if (packageRegistration != null && !packageRegistration.Owners.Contains(currentUser))
                throw new EntityException(Strings.PackageIdNotAvailable, nugetPackage.Id);

            if (packageRegistration == null) {
                packageRegistration = new PackageRegistration {
                    Id = nugetPackage.Id
                };

                packageRegistration.Owners.Add(currentUser);

                packageRegistrationRepo.InsertOnCommit(packageRegistration);
            }

            return packageRegistration;
        }

        Package CreatePackageFromNuGetPackage(PackageRegistration packageRegistration, IPackage nugetPackage) {
            var package = packageRegistration.Packages
                .Where(pv => pv.Version == nugetPackage.Version.ToString())
                .SingleOrDefault();

            if (package != null)
                throw new EntityException("A package with identifier '{0}' and version '{1}' already exists.", packageRegistration.Id, package.Version);

            // TODO: add flattened authors, and other properties
            // TODO: add package size
            var now = DateTime.UtcNow;
            var packageFileStream = nugetPackage.GetStream();

            package = new Package {
                Version = nugetPackage.Version.ToString(),
                Description = nugetPackage.Description,
                RequiresLicenseAcceptance = nugetPackage.RequireLicenseAcceptance,
                HashAlgorithm = cryptoSvc.HashAlgorithmId,
                Hash = cryptoSvc.GenerateHash(packageFileStream.ReadAllBytes()),
                PackageFileSize = packageFileStream.Length,
                Created = now,
                LastUpdated = now,
            };

            if (nugetPackage.IconUrl != null)
                package.IconUrl = nugetPackage.IconUrl.ToString();
            if (nugetPackage.LicenseUrl != null)
                package.LicenseUrl = nugetPackage.LicenseUrl.ToString();
            if (nugetPackage.ProjectUrl != null)
                package.ProjectUrl = nugetPackage.ProjectUrl.ToString();
            if (nugetPackage.Summary != null)
                package.Summary = nugetPackage.Summary;
            if (nugetPackage.Tags != null)
                package.Tags = nugetPackage.Tags;
            if (nugetPackage.Title != null)
                package.Title = nugetPackage.Title;

            foreach (var author in nugetPackage.Authors)
                package.Authors.Add(new PackageAuthor { Name = author });

            foreach (var dependency in nugetPackage.Dependencies)
                package.Dependencies.Add(new PackageDependency { Id = dependency.Id, VersionRange = dependency.VersionSpec.ToString() });

            package.FlattenedAuthors = package.Authors.Flatten();
            package.FlattenedDependencies = package.Dependencies.Flatten();

            return package;
        }

        void UpdateIsLatest(PackageRegistration packageRegistration) {
            // TODO: improve setting the latest bit; this is horrible. Trigger maybe?
            foreach (var pv in packageRegistration.Packages)
                pv.IsLatest = false;

            var latestVersion = packageRegistration.Packages.Max(pv => new Version(pv.Version));

            packageRegistration.Packages.Where(pv => pv.Version == latestVersion.ToString()).Single().IsLatest = true;
        }
    }
}