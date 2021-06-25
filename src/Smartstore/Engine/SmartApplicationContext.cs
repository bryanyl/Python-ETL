﻿using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Smartstore.Data;
using Smartstore.Engine.Modularity;
using Smartstore.Http;
using Smartstore.IO;
using Smartstore.Utilities;

namespace Smartstore.Engine
{
    public class SmartApplicationContext : IApplicationContext, IServiceProviderContainer
    {
        const string TempDirName = "_temp";

        private bool _freezed;
        private IDirectory _tempDirectory;
        private IDirectory _tempDirectoryTenant;

        public SmartApplicationContext(
            IHostEnvironment hostEnvironment,
            IConfiguration configuration,
            ILogger logger,
            params Assembly[] coreAssemblies)
        {
            Guard.NotNull(hostEnvironment, nameof(hostEnvironment));
            Guard.NotNull(configuration, nameof(configuration));
            Guard.NotNull(logger, nameof(logger));

            HostEnvironment = hostEnvironment;
            Configuration = configuration;
            Logger = logger;

            ConfigureFileSystem(hostEnvironment);

            DataSettings.SetApplicationContext(this, OnDataSettingsLoaded);

            ModuleCatalog = new ModuleCatalog();
            TypeScanner = new DefaultTypeScanner(ModuleCatalog, logger, coreAssemblies);
            OSIdentity = new GenericOSIdentity();

            // Create app configuration
            // TODO: (core) Try to incorporate IOptionsMonitor<SmartConfiguration> somehow.
            var config = new SmartConfiguration();
            configuration.Bind("Smartstore", config);

            AppConfiguration = config;
        }

        private void OnDataSettingsLoaded(DataSettings settings)
        {
            this.TenantRoot = settings.TenantRoot;
        }

        private void ConfigureFileSystem(IHostEnvironment hostEnvironment)
        {
            hostEnvironment.ContentRootFileProvider = new LocalFileSystem(hostEnvironment.ContentRootPath);

            if (hostEnvironment is IWebHostEnvironment we)
            {
                we.WebRootFileProvider = new LocalFileSystem(we.WebRootPath);
                WebRoot = (IFileSystem)we.WebRootFileProvider;
            }
            else
            {
                WebRoot = (IFileSystem)hostEnvironment.ContentRootFileProvider;
            }

            // TODO: (core) Read stuff from config and resolve tenant. Check folders and create them also.
            ThemesRoot = new LocalFileSystem(ContentRoot.MapPath("Themes"));
            ModulesRoot = new LocalFileSystem(ContentRoot.MapPath("Modules"));
            AppDataRoot = new LocalFileSystem(ContentRoot.MapPath("App_Data"));

            if (!AppDataRoot.DirectoryExists("Tenants"))
            {
                AppDataRoot.TryCreateDirectory("Tenants");
            }

            CommonHelper.ContentRoot = ContentRoot;
            WebHelper.WebRoot = WebRoot;
        }

        IServiceProvider IServiceProviderContainer.ApplicationServices { get; set; }

        public IHostEnvironment HostEnvironment { get; }
        public IConfiguration Configuration { get; }
        public ILogger Logger { get; }
        public SmartConfiguration AppConfiguration { get; }
        public ITypeScanner TypeScanner { get; }
        public IModuleCatalog ModuleCatalog { get; }

        public ILifetimeScope Services
        {
            get
            {
                var provider = ((IServiceProviderContainer)this).ApplicationServices;
                return provider?.AsLifetimeScope();
            }
        }

        public bool IsWebHost
        {
            get => HostEnvironment is IWebHostEnvironment;
        }

        public bool IsInstalled
        {
            get => DataSettings.DatabaseIsInstalled();
        }

        public string MachineName => Environment.MachineName;

        // Use the current host and the process id as two servers could run on the same machine
        public string EnvironmentIdentifier => Environment.MachineName + "-" + Environment.ProcessId;

        public IOSIdentity OSIdentity { get; }

        public IFileSystem ContentRoot => (IFileSystem)HostEnvironment.ContentRootFileProvider;
        public IFileSystem WebRoot { get; private set; }
        public IFileSystem ThemesRoot { get; private set; }
        public IFileSystem ModulesRoot { get; private set; }
        public IFileSystem AppDataRoot { get; private set; }
        public IFileSystem TenantRoot { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDirectory GetTempDirectory(string subDirectory = null)
        {
            return GetTempDirectoryInternal(AppDataRoot, ref _tempDirectory, subDirectory);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDirectory GetTenantTempDirectory(string subDirectory = null)
        {
            return GetTempDirectoryInternal(TenantRoot, ref _tempDirectoryTenant, subDirectory);
        }

        private static IDirectory GetTempDirectoryInternal(IFileSystem fs, ref IDirectory directory, string subDirectory)
        {
            if (directory == null)
            {
                fs.TryCreateDirectory(TempDirName);
                Interlocked.Exchange(ref directory, fs.GetDirectory(TempDirName));
            }

            if (subDirectory.HasValue())
            {
                var path = fs.PathCombine(TempDirName, subDirectory);
                fs.TryCreateDirectory(path);
                return fs.GetDirectory(TempDirName);
            }
            else
            {
                return directory;
            }
        }

        public void Freeze()
        {
            _freezed = true;
        }

        private void CheckFreezed()
        {
            if (_freezed)
            {
                throw new SmartException("Operation invalid after application has been bootstrapped completely.");
            }
        }
    }
}
