﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using QuestPatcher.Core;
using QuestPatcher.Core.Modding;
using QuestPatcher.Models;
using QuestPatcher.Services;
using QuestPatcher.Views;
using Serilog;

namespace QuestPatcher
{
    /// <summary>
    /// Handles creating browse dialogs for importing files, and also the importing of unknown files
    /// </summary>
    public class BrowseImportManager
    {
        private struct FileImportInfo
        {
            public string Path { get; set; }

            public FileCopyType? PreferredCopyType { get; set; }
        }

        private readonly OtherFilesManager _otherFilesManager;
        private readonly ModManager _modManager;
        private readonly Window _mainWindow;
        private readonly InstallManager _installManager;
        private readonly OperationLocker _locker;
        private readonly QuestPatcherUiService _uiService;

        private readonly FilePickerFileType _modsFilter = new("Quest Mods")
        {
            Patterns = new List<string>() { "*.qmod" }
        };

        private Queue<FileImportInfo>? _currentImportQueue;

        public BrowseImportManager(OtherFilesManager otherFilesManager, ModManager modManager, Window mainWindow, InstallManager installManager, OperationLocker locker, QuestPatcherUiService uiService)
        {
            _otherFilesManager = otherFilesManager;
            _modManager = modManager;
            _mainWindow = mainWindow;
            _installManager = installManager;
            _locker = locker;
            _uiService = uiService;
        }

        private static FilePickerFileType GetCosmeticFilter(FileCopyType copyType)
        {
            return new FilePickerFileType(copyType.NamePlural)
            {
                Patterns = copyType.SupportedExtensions.Select(extension => $"*.{extension}").ToList()
            };
        }

        /// <summary>
        /// Opens a browse dialog for installing mods only.
        /// </summary>
        /// <returns>A task that completes when the dialog has closed and the files have been imported</returns>
        public async Task ShowModsBrowse()
        {
            await ShowDialogAndHandleResult(new() { _modsFilter });
        }

        /// <summary>
        /// Opens a browse dialog for installing this particular type of file copy/cosmetic.
        /// </summary>
        /// <param name="cosmeticType"></param>
        /// <returns>A task that completes when the dialog has closed and the files have been imported</returns>
        public async Task ShowFileCopyBrowse(FileCopyType cosmeticType)
        {
            await ShowDialogAndHandleResult(new() { GetCosmeticFilter(cosmeticType) }, cosmeticType);
        }

        private async Task ShowDialogAndHandleResult(List<FilePickerFileType> filters, FileCopyType? knownFileCopyType = null)
        {
            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                FileTypeFilter = filters
            });

            if (files == null)
            {
                return;
            }

            await AttemptImportFiles(files.Select(file => file.Path.LocalPath).ToList(), knownFileCopyType);
        }

        /// <summary>
        /// Imports multiple files, and finds what type they are first.
        /// Will prompt the user with any errors while importing the files.
        /// If a list of files is already importing, these files will be added to the queue
        /// </summary>
        /// <param name="files">The paths of the files to import</param>
        /// <param name="preferredCopyType">File copy type that will be used if there are multiple copy types for one of these files. If null or not valid for the item, a dialog will be displayed allowing the user to choose</param>
        public async Task AttemptImportFiles(ICollection<string> files, FileCopyType? preferredCopyType = null)
        {
            bool queueExisted = _currentImportQueue != null;
            if (_currentImportQueue == null)
            {
                _currentImportQueue = new Queue<FileImportInfo>();
            }

            // Append all files to the new or existing queue
            Log.Debug("Enqueuing {FilesEnqueued} files", files.Count);
            foreach (string file in files)
            {
                _currentImportQueue.Enqueue(new FileImportInfo
                {
                    Path = file,
                    PreferredCopyType = preferredCopyType
                });
            }

            // If a queue already existed, that will be processed with our enqueued files, so we can stop here
            if (queueExisted)
            {
                Log.Debug("Queue is already being processed");
                return;
            }

            // Otherwise, we process the current queue
            Log.Debug("Processing queue . . .");

            // Do nothing if attempting to import files when operations are ongoing that are not file imports
            // TODO: Ideally this would wait until the lock is free and then continue
            if (!_locker.IsFree)
            {
                Log.Error("Failed to process files: Operations are still ongoing");
                _currentImportQueue = null;
                return;
            }
            _locker.StartOperation();
            try
            {
                await ProcessImportQueue();
            }
            finally
            {
                _locker.FinishOperation();
                _currentImportQueue = null;
            }
        }

        /// <summary>
        /// Processes the current import queue until it reaches zero in size.
        /// Displays exceptions for any failed files
        /// </summary>
        private async Task ProcessImportQueue()
        {
            if (_currentImportQueue == null)
            {
                throw new InvalidOperationException("Cannot process import queue if there is no import queue assigned");
            }

            // Attempt to import each file, and catch the exceptions if any to display them below
            Dictionary<string, Exception> failedFiles = new();
            int totalProcessed = 0; // We cannot know how many files were enqueued in total, so we keep track of that here
            while (_currentImportQueue.TryDequeue(out var importInfo))
            {
                string path = importInfo.Path;
                totalProcessed++;
                try
                {
                    Log.Information("Importing {ImportingPath} . . .", path);
                    await ImportUnknownFile(path, importInfo.PreferredCopyType);
                }
                catch (Exception ex)
                {
                    failedFiles[path] = ex;
                }
            }
            _currentImportQueue = null; // New files added should go to a new queue

            Log.Information("{SuccessfullyProcessed}/{TotalFilesProcessed} files imported successfully", totalProcessed - failedFiles.Count, totalProcessed);

            if (failedFiles.Count == 0) { return; }

            bool multiple = failedFiles.Count > 1;

            DialogBuilder builder = new()
            {
                Title = "Import Failed",
                HideCancelButton = true
            };

            if (multiple)
            {
                // Show the exceptions for multiple files in the logs to avoid a giagantic dialog
                builder.Text = "Multiple files failed to install. Check logs for details about each";
                foreach (var pair in failedFiles)
                {
                    Log.Error("Failed to install {FileName}: {Error}", Path.GetFileName(pair.Key), pair.Value.Message);
                    Log.Debug(pair.Value, "Full error");
                }
            }
            else
            {
                // Display single files with more detail for the user
                string filePath = failedFiles.Keys.First();
                var exception = failedFiles.Values.First();

                // Don't display the full stack trace for InstallationExceptions, since these are thrown by QP and are not bugs/issues
                if (exception is InstallationException)
                {
                    builder.Text = $"{Path.GetFileName(filePath)} failed to install: {exception.Message}";
                }
                else
                {
                    builder.Text = $"The file {Path.GetFileName(filePath)} failed to install";
                    builder.WithException(exception);
                }
                Log.Error("Failed to install {FileName}: {Error}", Path.GetFileName(filePath), exception.Message);
                Log.Debug(exception, "Full Error");
            }

            await builder.OpenDialogue(_mainWindow);
        }

        /// <summary>
        /// Figures out what the given file is, and installs it accordingly.
        /// Throws an exception if the file cannot be installed by QuestPatcher.
        /// </summary>
        /// <param name="path">The path of file to import</param>
        /// <param name="preferredCopyType">File copy type that will be used if there are multiple copy types for this file. If null, a dialog will be displayed allowing the user to choose</param>
        private async Task ImportUnknownFile(string path, FileCopyType? preferredCopyType)
        {
            string extension = Path.GetExtension(path).ToLower();

            // Attempt to install as a mod first
            if (await TryImportMod(path))
            {
                return;
            }

            // Attempt to copy the file to the quest as a map, hat or similar
            List<FileCopyType> copyTypes;
            if (preferredCopyType == null || !preferredCopyType.SupportedExtensions.Contains(extension[1..]))
            {
                copyTypes = _otherFilesManager.GetFileCopyTypes(extension);
            }
            else
            {
                // If we already know the file copy type
                // e.g. from dragging into a particular part of the UI, or for browsing for a particular file type,
                // we don't need to prompt on which file copy type to use
                copyTypes = new() { preferredCopyType };
            }

            if (copyTypes.Count > 0)
            {
                FileCopyType copyType;
                if (copyTypes.Count > 1)
                {
                    // If there are multiple different file copy types for this file, prompt the user to decide what they want to import it as
                    var chosen = await OpenSelectCopyTypeDialog(copyTypes, path);
                    if (chosen == null)
                    {
                        Log.Information("No file type selected, cancelling import of {FileName}", Path.GetFileName(path));
                        return;
                    }
                    else
                    {
                        copyType = chosen;
                    }
                }
                else
                {
                    // Otherwise, just use the only type available
                    copyType = copyTypes[0];
                }

                await copyType.PerformCopy(path);
                return;
            }

            throw new InstallationException($"Unrecognised file type {extension}");
        }

        /// <summary>
        /// Opens a dialog to allow the user to choose between multiple different file copy destinations to import a file as.
        /// </summary>
        /// <param name="copyTypes">The available file copy types for this file</param>
        /// <param name="path">The path of the file</param>
        /// <returns>The selected FileCopyType, or null if the user pressed cancel/closed the dialog</returns>
        private async Task<FileCopyType?> OpenSelectCopyTypeDialog(List<FileCopyType> copyTypes, string path)
        {
            FileCopyType? selectedType = null;

            DialogBuilder builder = new()
            {
                Title = "Multiple Import Options",
                Text = $"{Path.GetFileName(path)} can be imported as multiple types of file. Please select what you would like it to be installed as.",
                HideOkButton = true,
                HideCancelButton = true
            };

            List<ButtonInfo> dialogButtons = new();
            foreach (var copyType in copyTypes)
            {
                dialogButtons.Add(new ButtonInfo
                {
                    ReturnValue = true,
                    CloseDialogue = true,
                    OnClick = () =>
                    {
                        selectedType = copyType;
                    },
                    Text = copyType.NameSingular
                });
            }
            builder.WithButtons(dialogButtons);

            await builder.OpenDialogue(_mainWindow);
            return selectedType;
        }

        /// <summary>
        /// Imports then installs a mod.
        /// Will prompt to ask the user if they want to install the mod in the case that it is outdated
        /// </summary>
        /// <param name="path">The path of the mod</param>
        /// <returns>Whether or not the file could be imported as a mod</returns>
        private async Task<bool> TryImportMod(string path)
        {
            // Import the mod file and copy it to the quest
            var mod = await _modManager.TryParseMod(path);
            if (mod is null)
            {
                return false;
            }

            if (mod.ModLoader != _installManager.InstalledApp?.ModLoader)
            {
                DialogBuilder builder = new()
                {
                    Title = "Wrong Mod Loader",
                    Text = $"The mod you are trying to install needs the modloader {mod.ModLoader}, however your app has the modloader {_installManager.InstalledApp?.ModLoader} installed."
                    + "\nWould you like to repatch your app with the required modloader?"
                };
                builder.OkButton.Text = "Repatch";
                builder.CancelButton.Text = "Not now";
                if (await builder.OpenDialogue(_mainWindow))
                {
                    _uiService.OpenRepatchMenu(mod.ModLoader);
                }

                return true;
            }

            Debug.Assert(_installManager.InstalledApp != null);

            // Prompt the user for outdated mods instead of enabling them automatically
            if (mod.PackageVersion != null && mod.PackageVersion != _installManager.InstalledApp.Version)
            {
                DialogBuilder builder = new()
                {
                    Title = "Outdated Mod",
                    Text = $"The mod just installed is for version {mod.PackageVersion} of your app, however you have {_installManager.InstalledApp.Version}. Enabling the mod may crash the game, or not work."
                };
                builder.OkButton.Text = "Enable Now";
                builder.CancelButton.Text = "Cancel";

                if (!await builder.OpenDialogue(_mainWindow))
                {
                    return true;
                }
            }

            // Automatically install the mod once it has been imported
            // TODO: Is this desirable? Would it make sense to require it to be enabled manually
            await mod.Install();
            await _modManager.SaveMods();
            return true;
        }
    }
}
