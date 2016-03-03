﻿using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using Magician.Connect;
using Magician.WebResourceLiveSync.Data;
using Magician.WebResourceLiveSync.Helpers;
using Magician.WebResourceLiveSync.Model;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Encoder = Magician.WebResourceLiveSync.Helpers.Encoder;

namespace Magician.WebResourceLiveSync.ViewModels
{
    public class ControlViewModel : ViewModelBase
    {
        private ObservableCollection<Solution> _solutions;
        public ObservableCollection<Solution> Solutions
        {
            get { return _solutions; }
            set { Set(ref _solutions, value); }
        }

        private Solution _selectedSolution;
        public Solution SelectedSolution
        {
            get { return _selectedSolution; }
            set
            {
                Set(ref _selectedSolution, value);

                if (!string.IsNullOrEmpty(LocalDirectory))
                {
                    RefreshResourceStates();
                }
            }
        }

        private string _localDirectory;
        public string LocalDirectory
        {
            get { return _localDirectory; }
            set
            {
                Set(ref _localDirectory, value);

                RefreshTree();
            }
        }

        private ObservableCollection<DirectoryItem> _files;
        public ObservableCollection<DirectoryItem> Files
        {
            get { return _files; }
            set { Set(ref _files, value); }
        }

        private string _connectText = "Connect";
        public string ConnectText
        {
            get { return _connectText; }
            set { Set(ref _connectText, value); }
        }

        public string AutoSyncState
        {
            get
            {
                return _isAutoSyncEnabled == true ?
                    "Enabled" : "Disabled";
            }
        }

        private bool _isBusy = false;
        public bool IsBusy
        {
            get { return _isBusy; }
            set { Set(ref _isBusy, value); }
        }

        private bool _isConnected = false;
        public bool IsConnected
        {
            get { return _isConnected; }
            set { Set(ref _isConnected, value); }
        }

        public ICommand ConnectCommand { get; private set; }
        public ICommand ToggleAutoSyncCommand { get; private set; }

        private bool _areResourcesMapped = false;
        private bool _isAutoSyncEnabled = false;

        private Connector _connector;

        private CrmService _service;

        private Messenger _messenger;

        private FileWatcher _watcher;

        private Toaster _toaster;

        // TODO: file comparison for images needs byte-level comparison
        // TODO: autosync creates and updates(on/offable w/ completed notification)
        // TODO: compare modifiedby user and datetime before upload
        // TODO: handle delete/rename resource
        // TODO: handle resume autosync
        // TODO: store profiles solution/directory combo (future)
        // TODO: download resources
        public ControlViewModel(Messenger messenger)
        {
            _messenger = messenger;
            _toaster = new Toaster();

            Files = new ObservableCollection<DirectoryItem>();

            ConnectCommand = new RelayCommand(() => Connect());
            ToggleAutoSyncCommand = new RelayCommand(() => ToggleAutoSync());
        }

        private async void Connect()
        {
            IsConnected = false;

            if (_connector == null)
            {
                _connector = new Connector();
            }

            if (!_connector.Connect())
            {
                MessageBox.Show("Please click Connect to try connecting to Dynamics CRM again. A valid connection is required.");
                return;
            }

            IsBusy = true;

            _service = new CrmService(_connector.OrganizationServiceProxy);

            ConnectText = "Reconnect";

            _messenger.Send(new UpdateHeaderMessage
            {
                Header = "Web Resource Live Sync: " + _connector.OrganizationFriendlyName
            });

            var solutions = await LoadSolutions();

            Solutions = new ObservableCollection<Solution>(solutions);

            IsConnected = true;
            IsBusy = false;
        }

        private Task<IEnumerable<Solution>> LoadSolutions()
        {
            return Task.Run(() =>
            {
                return _service.RetrieveSolutions();
            });
        }

        private void ToggleAutoSync()
        {
            _isAutoSyncEnabled = !_isAutoSyncEnabled;

            RaisePropertyChanged(() => AutoSyncState);
        }

        private async Task RefreshTree()
        {
            IsBusy = true;

            if (Files.Count > 0)
            {
                Files.RemoveAt(0);
            }

            if (Directory.Exists(LocalDirectory))
            {
                if (!LocalDirectory.EndsWith("\\"))
                {
                    _localDirectory = _localDirectory + "\\";
                }

                var localDirectory = new Uri(LocalDirectory);

                var directoryInfo = await ScanDirectory(localDirectory);

                Files.Add(directoryInfo);

                if (SelectedSolution != null)
                {
                    await RefreshResourceStates(directoryInfo);
                }

                SetupFilewatcher();
            }

            IsBusy = false;
        }

        private Task<DirectoryItem> ScanDirectory(Uri localDirectory)
        {
            return Task.Run(() =>
            {
                var rootDirectoryInfo = new DirectoryInfo(localDirectory.OriginalString);

                return GetContents(localDirectory, rootDirectoryInfo);
            });
        }

        private DirectoryItem GetContents(Uri localDirectory, DirectoryInfo directoryInfo)
        {
            var directoryNode = new FolderItem { Name = directoryInfo.Name };

            foreach (var directory in directoryInfo.GetDirectories())
            {
                directoryNode.Items.Add(GetContents(localDirectory, directory));
            }

            foreach (var file in directoryInfo.GetFiles())
            {
                var item = file.ConvertToFile(localDirectory);

                if (item.IsValid)
                {
                    directoryNode.Items.Add(item);
                }
            }

            return directoryNode;
        }

        private async Task RefreshResourceStates(DirectoryItem directoryInfo = null)
        {
            _areResourcesMapped = false;

            await MapResources(directoryInfo);

            _areResourcesMapped = true;
        }

        private async Task MapResources(DirectoryItem directoryInfo)
        {
            if (directoryInfo == null && Files != null && Files.Count > 0)
            {
                directoryInfo = Files.First();
            }
            if (directoryInfo == null || directoryInfo.Items == null || directoryInfo.Items.Count == 0)
            {
                return;
            }

            foreach (var item in directoryInfo.Items)
            {
                await MapResource(item);
            }
        }

        private async Task MapResource(DirectoryItem item)
        {
            if (item.IsFile)
            {
                var file = (FileItem)item;

                file.IsSynching = true;
                file.IsUpToDate = file.IsOutOfDate = false;

                var result = await CheckExistingResource(file);

                if (result != null)
                {
                    file.IsUpToDate = result.IsMatch;
                    file.IsOutOfDate = !result.IsMatch;
                    file.ResourceId = result.ResourceId;
                }

                item.IsSynching = false;
            }
            else
            {
                await MapResources(item);
            }
        }

        private Task<CompareResult> CheckExistingResource(FileItem item)
        {
            return Task.Run(() =>
            {
                var matches = _service.SearchWebResourcesByName(
                    SelectedSolution.CustomizationPrefix + "_" + item.RelativePath.ToString(),
                    SelectedSolution.CustomizationPrefix + "_/" + item.RelativePath.ToString())
                    .ToList();

                if (matches != null && matches.Count == 1)
                {
                    var resource = matches[0];

                    var fileText = File.ReadAllText(item.FullName.OriginalString);

                    var resourceText = Encoder.DecodeBas64(resource["content"] as string);

                    return new CompareResult
                    {
                        ResourceId = resource.Id,
                        IsMatch = fileText == resourceText
                    };
                }
                else if (matches != null && matches.Count > 1)
                {
                    throw new Exception("Too many matches found for resource at: " + item.FullName);
                }

                return null;
            });
        }

        private void SetupFilewatcher()
        {
            DestroyFilewatcher();

            _watcher = new FileWatcher(LocalDirectory, true);

            _watcher.Handler = FileWatchUpate;
        }

        private void DestroyFilewatcher()
        {
            if (_watcher != null)
            {
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private async void FileWatchUpate(object sender, FileSystemEventArgs e)
        {
            //_toaster.Show("Web Resource Live Sync", "File changed.");

            if (e.ChangeType == WatcherChangeTypes.Created ||
                e.ChangeType == WatcherChangeTypes.Renamed ||
                e.ChangeType == WatcherChangeTypes.Deleted)
            {
                await RefreshTree();
                return;
            }

            var fileItem = FindFileItemByPath(e.FullPath);

            // If the file was not found in our tree, ignore it
            if (fileItem == null)
            {
                return;
            }

            var fileLastWriteTime = File.GetLastWriteTimeUtc(e.FullPath);

            // If the file's last updated time stamp is the same, ignore this event
            if (fileItem.LastWriteTime == fileLastWriteTime)
            {
                return;
            }

            fileItem.LastWriteTime = fileLastWriteTime;

            await MapResource(fileItem);

            await UpdateResource(fileItem);
        }

        private FileItem FindFileItemByPath(string fullName)
        {
            if (Files == null || Files.Count == 0)
            {
                return null;
            }

            var root = Files.First();
            var result = root.FindItemByPath(fullName);

            if (result != null && result is FileItem)
            {
                return (FileItem)result;
            }

            return null;
        }

        private async Task UpdateResource(FileItem fileItem)
        {
            if (SelectedSolution == null)
            {
                return;
            }

            var entity = fileItem.ConvertToEntity(SelectedSolution.CustomizationPrefix);

            await Update(entity);
        }

        private Task Create(Entity entity)
        {
            return Task.Run(() =>
            {
                _service.Create(entity);
            });
        }

        private Task Update(Entity entity)
        {
            return Task.Run(() =>
            {
                _service.Update(entity);
            });
        }
    }
}
