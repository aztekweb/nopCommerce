﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Nop.Core;
using Nop.Core.ComponentModel;
using Nop.Core.Configuration;
using Nop.Core.Infrastructure;
using Nop.Data.Mapping;
using Nop.Services.Plugins;

namespace Nop.Web.Framework.Infrastructure.Extensions
{
    /// <summary>
    /// Represents application part manager extensions
    /// </summary>
    public static partial class ApplicationPartManagerExtensions
    {
        #region Fields

        private static readonly INopFileProvider _fileProvider;
        private static readonly List<string> _baseAppLibraries;
        private static readonly Dictionary<string, PluginLoadedAssemblyInfo> _loadedAssemblies = new();
        private static readonly ReaderWriterLockSlim _locker = new();

        #endregion

        #region Ctor

        static ApplicationPartManagerExtensions()
        {
            //we use the default file provider, since the DI isn't initialized yet
            _fileProvider = CommonHelper.DefaultFileProvider;

            _baseAppLibraries = new List<string>();

            //get all libraries from /bin/{version}/ directory
            _baseAppLibraries.AddRange(_fileProvider.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll")
                .Select(fileName => _fileProvider.GetFileName(fileName)));

            //get all libraries from base site directory
            if (!AppDomain.CurrentDomain.BaseDirectory.Equals(Environment.CurrentDirectory, StringComparison.InvariantCultureIgnoreCase))
            {
                _baseAppLibraries.AddRange(_fileProvider.GetFiles(Environment.CurrentDirectory, "*.dll")
                    .Select(fileName => _fileProvider.GetFileName(fileName)));
            }

            //get all libraries from refs directory
            var refsPathName = _fileProvider.Combine(Environment.CurrentDirectory, NopPluginDefaults.RefsPathName);
            if (_fileProvider.DirectoryExists(refsPathName))
            {
                _baseAppLibraries.AddRange(_fileProvider.GetFiles(refsPathName, "*.dll")
                    .Select(fileName => _fileProvider.GetFileName(fileName)));
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets access to information about plugins
        /// </summary>
        private static IPluginsInfo PluginsInfo
        {
            get => Singleton<IPluginsInfo>.Instance;
            set => Singleton<IPluginsInfo>.Instance = value;
        }

        #endregion

        #region Utilities
        
        /// <summary>
        /// Load and register the assembly
        /// </summary>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <param name="assemblyFile">Path to the assembly file</param>
        /// <param name="useUnsafeLoadAssembly">Indicating whether to load an assembly into the load-from context, bypassing some security checks</param>
        /// <returns>Assembly</returns>
        private static Assembly AddApplicationParts(ApplicationPartManager applicationPartManager, string assemblyFile, bool useUnsafeLoadAssembly)
        {
            //try to load a assembly
            Assembly assembly;

            try
            {
                assembly = Assembly.LoadFrom(assemblyFile);
            }
            catch (FileLoadException)
            {
                if (useUnsafeLoadAssembly)
                {
                    //if an application has been copied from the web, it is flagged by Windows as being a web application,
                    //even if it resides on the local computer.You can change that designation by changing the file properties,
                    //or you can use the<loadFromRemoteSources> element to grant the assembly full trust.As an alternative,
                    //you can use the UnsafeLoadFrom method to load a local assembly that the operating system has flagged as
                    //having been loaded from the web.
                    //see http://go.microsoft.com/fwlink/?LinkId=155569 for more information.
                    assembly = Assembly.UnsafeLoadFrom(assemblyFile);
                }
                else
                    throw;
            }

            //register the plugin definition
            applicationPartManager.ApplicationParts.Add(new AssemblyPart(assembly));

            return assembly;
        }

        /// <summary>
        /// Perform file deploy and return loaded assembly
        /// </summary>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <param name="assemblyFile">Path to the plugin assembly file</param>
        /// <param name="pluginConfig">Plugin config</param>
        /// <param name="fileProvider">Nop file provider</param>
        /// <returns>Assembly</returns>
        private static Assembly PerformFileDeploy(this ApplicationPartManager applicationPartManager,
            string assemblyFile, PluginConfig pluginConfig, INopFileProvider fileProvider)
        {
            //ensure for proper directory structure
            if (string.IsNullOrEmpty(assemblyFile) ||
                string.IsNullOrEmpty(fileProvider.GetParentDirectory(assemblyFile)))
                throw new InvalidOperationException(
                    $"The plugin directory for the {fileProvider.GetFileName(assemblyFile)} file exists in a directory outside of the allowed nopCommerce directory hierarchy");

            var assembly =
                AddApplicationParts(applicationPartManager, assemblyFile, pluginConfig.UseUnsafeLoadAssembly);

            // delete the .deps file
            if (assemblyFile.EndsWith(".dll")) 
                _fileProvider.DeleteFile(assemblyFile[0..^4] + ".deps.json");

            return assembly;
        }

        /// <summary>
        /// Check whether the assembly is already loaded
        /// </summary>
        /// <param name="filePath">Assembly file path</param>
        /// <param name="pluginName">Plugin system name</param>
        /// <returns>Result of check</returns>
        private static bool IsAlreadyLoaded(string filePath, string pluginName)
        {
            //ignore already loaded libraries
            //(we do it because not all libraries are loaded immediately after application start)
            var fileName = _fileProvider.GetFileName(filePath);
            if (_baseAppLibraries.Any(library => library.Equals(fileName, StringComparison.InvariantCultureIgnoreCase)))
                return true;

            try
            {
                //get filename without extension
                var fileNameWithoutExtension = _fileProvider.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(fileNameWithoutExtension))
                    throw new Exception($"Cannot get file extension for {fileName}");

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    //compare assemblies by file names
                    var assemblyName = (assembly.FullName ?? string.Empty).Split(',').FirstOrDefault();
                    if (!fileNameWithoutExtension.Equals(assemblyName, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    //loaded assembly not found
                    if (!_loadedAssemblies.ContainsKey(assemblyName))
                    {
                        //add it to the list to find collisions later
                        _loadedAssemblies.Add(assemblyName, new PluginLoadedAssemblyInfo(assemblyName, assembly.FullName));
                    }

                    //set assembly name and plugin name for further using
                    _loadedAssemblies[assemblyName].References.Add((pluginName, AssemblyName.GetAssemblyName(filePath).FullName));

                    return true;
                }
            }
            catch
            {
                // ignored
            }

            //nothing found
            return false;
        }
        
        #endregion

        #region Methods

        /// <summary>
        /// Initialize plugins system
        /// </summary>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <param name="pluginConfig">Plugin config</param>
        public static void InitializePlugins(this ApplicationPartManager applicationPartManager, PluginConfig pluginConfig)
        {
            if (applicationPartManager == null)
                throw new ArgumentNullException(nameof(applicationPartManager));

            if (pluginConfig == null)
                throw new ArgumentNullException(nameof(pluginConfig));

            //perform with locked access to resources
            using (new ReaderWriteLockDisposable(_locker))
            {
                try
                {
                    //ensure plugins directory is created
                    var pluginsDirectory = _fileProvider.MapPath(NopPluginDefaults.Path);
                    _fileProvider.CreateDirectory(pluginsDirectory);
                    
                    //ensure uploaded directory is created
                    var uploadedPath = _fileProvider.MapPath(NopPluginDefaults.UploadedPath);
                    _fileProvider.CreateDirectory(uploadedPath);

                    foreach (var directory in _fileProvider.GetDirectories(uploadedPath))
                    {
                        var moveTo = _fileProvider.Combine(pluginsDirectory, _fileProvider.GetDirectoryNameOnly(directory));
                        
                        if (_fileProvider.DirectoryExists(moveTo))
                            _fileProvider.DeleteDirectory(moveTo);

                        _fileProvider.DirectoryMove(directory, moveTo);
                    }

                    PluginsInfo = new PluginsInfo(_fileProvider);
                    PluginsInfo.LoadPluginInfo();

                    foreach (var pluginDescriptor in PluginsInfo.PluginDescriptors.Where(p => p.needToDeploy)
                                 .Select(p => p.pluginDescriptor))
                    {
                        var mainPluginFile = pluginDescriptor.OriginalAssemblyFile;

                        //try to deploy main plugin assembly 
                        pluginDescriptor.ReferencedAssembly =
                            applicationPartManager.PerformFileDeploy(mainPluginFile, pluginConfig, _fileProvider);

                        //and then deploy all other referenced assemblies
                        var filesToDeploy = pluginDescriptor.PluginFiles.Where(file =>
                            !_fileProvider.GetFileName(file).Equals(_fileProvider.GetFileName(mainPluginFile)) &&
                            !IsAlreadyLoaded(file, pluginDescriptor.SystemName)).ToList();

                        foreach (var file in filesToDeploy)
                            applicationPartManager.PerformFileDeploy(file, pluginConfig, _fileProvider);

                        //determine a plugin type (only one plugin per assembly is allowed)
                        var pluginType = pluginDescriptor.ReferencedAssembly.GetTypes().FirstOrDefault(type =>
                            typeof(IPlugin).IsAssignableFrom(type) && !type.IsInterface && type.IsClass &&
                            !type.IsAbstract);

                        if (pluginType != default)
                            pluginDescriptor.PluginType = pluginType;
                    }
                }
                catch (Exception exception)
                {
                    //throw full exception
                    var message = string.Empty;
                    for (var inner = exception; inner != null; inner = inner.InnerException)
                        message = $"{message}{inner.Message}{Environment.NewLine}";

                    throw new Exception(message, exception);
                }
                
                PluginsInfo.AssemblyLoadedCollision = _loadedAssemblies.Select(item => item.Value)
                    .Where(loadedAssemblyInfo => loadedAssemblyInfo.Collisions.Any()).ToList();

                //add name compatibility types from plugins
                var nameCompatibilityList = PluginsInfo.PluginDescriptors.Where(pd => pd.pluginDescriptor.Installed).SelectMany(pd => pd
                    .pluginDescriptor.ReferencedAssembly.GetTypes().Where(type =>
                        typeof(INameCompatibility).IsAssignableFrom(type) && !type.IsInterface && type.IsClass &&
                        !type.IsAbstract));
                NameCompatibilityManager.AdditionalNameCompatibilities.AddRange(nameCompatibilityList);
            }
        }

        #endregion
    }
}
