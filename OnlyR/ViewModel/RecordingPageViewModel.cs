﻿using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OnlyR.Core.Enums;
using OnlyR.Exceptions;
using OnlyR.Model;
using OnlyR.Services.Audio;
using OnlyR.Services.Options;
using OnlyR.Services.RecordingCopies;
using OnlyR.Services.RecordingDestination;
using OnlyR.Services.Snackbar;
using OnlyR.Utils;
using OnlyR.ViewModel.Messages;
using Serilog;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using Fclp.Internals.Extensions;
using OnlyR.Core.EventArgs;

namespace OnlyR.ViewModel
{
    /// <summary>
    /// View model for Recording page. Contains properties that the Recording page 
    /// can data bind to, i.e. it has everything that is needed by the user during 
    /// interaction with the Recording page.
    /// </summary>
    public class RecordingPageViewModel : ObservableObject, IPage
    {
        private readonly IAudioService _audioService;
        private readonly IRecordingDestinationService _destinationService;
        private readonly IOptionsService _optionsService;
        private readonly ICopyRecordingsService _copyRecordingsService;
        private readonly ICommandLineService _commandLineService;
        private readonly ISnackbarService _snackbarService;
        private readonly ulong _safeMinBytesFree = 0x20000000;  // 0.5GB
        private readonly Stopwatch _stopwatch;
        private readonly ConcurrentDictionary<char, DateTime> _removableDrives = new();
        private readonly Queue<VolumeSample> _volumeAverageForSilence = new Queue<VolumeSample>();
        private readonly Queue<VolumeSample> _volumeAverageForChunk = new Queue<VolumeSample>();
        private readonly int _minVolumeAverage = 5;  // TODO: This could be moved to a config file to allow tweeking
        private readonly int _validInputLevel = 10;  // TODO: This could be moved to a config file to allow tweeking
        private readonly int _silenceSecToRestart = 5;  // TODO: This could be moved to a config file to allow tweeking

        private readonly int _chunkDetectionVolumeThreshold = 3;
        private readonly int _chunkMinimumSecond = 2;

        private DispatcherTimer? _splashTimer;
        private int _volumeLevel;
        private int _maxAverageVolumeLevel;
        private bool _isCopying;
        private RecordingStatus _recordingStatus;
        private string _statusStr;
        private string? _errorMsg;

        enum eStopModes {
            Nothing = 0,
            JustStop,
            CopyRestart,
            DeleteRestart
        }
        private eStopModes _eStopMode;

        public RecordingPageViewModel(
            IAudioService audioService,
            IOptionsService optionsService,
            ICommandLineService commandLineService,
            IRecordingDestinationService destinationService,
            ICopyRecordingsService copyRecordingsService,
            ISnackbarService snackbarService)
        {
            WeakReferenceMessenger.Default.Register<BeforeShutDownMessage>(this, OnShutDown);
            WeakReferenceMessenger.Default.Register<SessionEndingMessage>(this, OnSessionEnding);
            WeakReferenceMessenger.Default.Register<NavigateMessage>(this, OnNavigate);

            _commandLineService = commandLineService;
            _copyRecordingsService = copyRecordingsService;
            _snackbarService = snackbarService;

            _stopwatch = new Stopwatch();

            _audioService = audioService;
            _audioService.StartedEvent += AudioStartedHandler;
            _audioService.StoppedEvent += AudioStoppedHandler;
            _audioService.StopCompleteEvent += StopCompleteHandler;
            _audioService.StopRequested += AudioStopRequestedHandler;
            _audioService.RecordingProgressEvent += AudioProgressHandler;

            _optionsService = optionsService;
            _destinationService = destinationService;
            _recordingStatus = RecordingStatus.NotRecording;

            _statusStr = Properties.Resources.NOT_RECORDING;

            // bind commands...
            StartRecordingCommand = new RelayCommand(StartRecording);
            StopRecordingCommand = new RelayCommand(StopRecording);
            NavigateSettingsCommand = new RelayCommand(NavigateSettings);
            ShowRecordingsCommand = new RelayCommand(ShowRecordings);
            SaveToRemovableDriveCommand = new RelayCommand(SaveToRemovableDrives);

            WeakReferenceMessenger.Default.Register<RemovableDriveMessage>(this, OnRemovableDriveMessage);
        }

        private void OnNavigate(object recipient, NavigateMessage message)
        {
            if (message.OriginalPageName == SettingsPageViewModel.PageName 
                && message.TargetPageName == PageName)
            {
                OnPropertyChanged(nameof(MaxRecordingTimeString));
                OnPropertyChanged(nameof(IsMaxRecordingTimeSpecified));
            }
        }

        public static string PageName => "RecordingPage";

        // Commands (bound in ctor)...
        public RelayCommand StartRecordingCommand { get; }

        public RelayCommand StopRecordingCommand { get; }

        public RelayCommand NavigateSettingsCommand { get; }

        public RelayCommand ShowRecordingsCommand { get; }

        public RelayCommand SaveToRemovableDriveCommand { get; }

        public bool IsCopying
        {
            get => _isCopying;
            set
            {
                if (_isCopying != value)
                {
                    _isCopying = value;
                    OnPropertyChanged(nameof(IsCopying));
                    OnPropertyChanged(nameof(IsSaveEnabled));
                }
            }
        }

        public bool IsRecordingOrStopping => RecordingStatus == RecordingStatus.Recording ||
                                             RecordingStatus == RecordingStatus.StopRequested;

        public bool IsNotRecording => RecordingStatus == RecordingStatus.NotRecording;

        public bool IsRecording => RecordingStatus == RecordingStatus.Recording;

        public bool IsReadyToRecord => RecordingStatus != RecordingStatus.Recording &&
                                       RecordingStatus != RecordingStatus.StopRequested;

        public int VolumeLevelAsPercentage
        {
            get => _volumeLevel;
            set
            {
                if (_volumeLevel != value)
                {
                    _volumeLevel = value;
                    OnPropertyChanged(nameof(VolumeLevelAsPercentage));
                }
            }
        }

        public string? MaxRecordingTimeString =>
            _optionsService.Options.MaxRecordingTimeSeconds == 0
            ? null
            : TimeSpan.FromSeconds(_optionsService.Options.MaxRecordingTimeSeconds).ToString("hh\\:mm\\:ss", CultureInfo.CurrentCulture);

        public bool IsMaxRecordingTimeSpecified => _optionsService.Options.MaxRecordingTimeSeconds > 0;

        public string ElapsedTimeStr => _ElapsedTime.ToString("hh\\:mm\\:ss", CultureInfo.CurrentCulture);

        public bool NoSettings => _commandLineService.NoSettings;

        public bool NoFolder => _commandLineService.NoFolder;

        public bool NoSave => _commandLineService.NoSave;

        public bool IsSaveVisible => !NoSave && !_removableDrives.IsEmpty;

        public bool IsSaveEnabled => !IsCopying && !IsRecordingOrStopping;

        /// <summary>
        /// Gets or sets the Recording status.
        /// </summary>
        public RecordingStatus RecordingStatus
        {
            get => _recordingStatus;
            set
            {
                if (_recordingStatus != value)
                {
                    _recordingStatus = value;
                    StatusStr = value.GetDescriptiveText();

                    OnPropertyChanged(nameof(RecordingStatus));
                    OnPropertyChanged(nameof(IsNotRecording));
                    OnPropertyChanged(nameof(IsRecording));
                    OnPropertyChanged(nameof(IsReadyToRecord));
                    OnPropertyChanged(nameof(IsRecordingOrStopping));
                    OnPropertyChanged(nameof(IsSaveEnabled));
                }
            }
        }

        /// <summary>
        /// Gets or sets the Recording Status as a string.
        /// </summary>
        public string StatusStr
        {
            get => _statusStr;
            set
            {
                if (_statusStr != value)
                {
                    _statusStr = value;
                    OnPropertyChanged(nameof(StatusStr));
                }
            }
        }

        public string? ErrorMsg
        {
            get => _errorMsg;
            set
            {
                if (_errorMsg != value)
                {
                    _errorMsg = value;
                    OnPropertyChanged(nameof(ErrorMsg));
                }
            }
        }

        public string SaveHint
        {
            get
            {
                var driveLetterList = string.Join(", ", _removableDrives.Keys);
                return string.IsNullOrEmpty(driveLetterList) ? string.Empty : $"{Properties.Resources.SAVE_TO_DRIVES} - {driveLetterList}";
            }
        }

        private TimeSpan _ElapsedTime => _stopwatch.IsRunning ? _stopwatch.Elapsed : TimeSpan.Zero;

        //private bool StopOnSilenceEnabled => _optionsService.Options.MaxSilenceTimeSeconds > 0;

        /// <summary>
        /// Responds to activation.
        /// </summary>
        /// <param name="state">RecordingPageNavigationState object (or null).</param>
        public void Activated(object? state)
        {
            // on display of page...
            var stateObj = (RecordingPageNavigationState?)state;
            if (stateObj != null)
            {
                if (stateObj.StartRecording)
                {
                    StartRecording();
                }
                else if (stateObj.ShowSplash)
                {
                    DoSplash();
                }
            }
        }

        public void Closing(object sender, CancelEventArgs e)
        {
            // prevent window closing when recording...
            e.Cancel = RecordingStatus != RecordingStatus.NotRecording;
        }

        private void NavigateSettings()
        {
            WeakReferenceMessenger.Default.Send(new NavigateMessage(PageName, SettingsPageViewModel.PageName, null));
        }

        private void OnShutDown(object recipient, BeforeShutDownMessage message)
        {
            // nothing to do
        }

        private void OnSessionEnding(object recipient, SessionEndingMessage e)
        {
            // allow the session to shutdown if we're not recording
            e.SessionEndingArgs.Cancel = RecordingStatus != RecordingStatus.NotRecording;
        }

        private void AudioProgressHandler(object? sender, RecordingProgressEventArgs e)
        {
            VolumeLevelAsPercentage = e.VolumeLevelAsPercentage;
            OnPropertyChanged(nameof(ElapsedTimeStr));

            if (RecordingStatus != RecordingStatus.StopRequested)
            {
                if (_optionsService.Options.MaxRecordingTimeSeconds > 0 &&
                    _ElapsedTime.TotalSeconds > _optionsService.Options.MaxRecordingTimeSeconds)
                {
                    AutoStopRecordingAtLimit();
                }

                //Log.Logger.Information($"Vol = {_volumeLevel} ElapsedTime.TotalSeconds = {_ElapsedTime.TotalSeconds}");

                // Silence detection
                if (_optionsService.Options.StopOnSilence)
                {
                    _eStopMode = StopIfSilenceDetected();

                    if (_optionsService.Options.SplitRecordingByChunk && _eStopMode == eStopModes.Nothing)
                    {
                        // Chunk detection
                        _eStopMode = StopIfChunkDetected();
                    }
                }

            }
        }

        private eStopModes StopIfSilenceDetected()
        {
            var currentTime = DateTime.UtcNow;

            // Only keep last SilenceSeconds + 1 seconds for average calculation
            int average = GetInputVolumeAverage(_volumeAverageForSilence, currentTime, (_optionsService.Options.SilencePeriod + 1) * 1000);
            _maxAverageVolumeLevel = Math.Max(average, _maxAverageVolumeLevel);
            //Log.Logger.Information($"{(_optionsService.Options.SilencePeriod + 1) * 1000}ms average = {average}, _maxAverageVolumeLevel = {_maxAverageVolumeLevel}");

            // Only check average if we have at least SilenceSeconds of volume data
            bool hasbeenSilent = _volumeAverageForSilence.Count > 0 && _volumeAverageForSilence.Peek().Timestamp < currentTime.AddSeconds(_optionsService.Options.SilencePeriod * -1) && average < _minVolumeAverage;
            if (hasbeenSilent)
            {
                if (HasAudioDetected())
                {
                    StopCommand($"[Audio detected] Automatically stopped recording having detected {_optionsService.Options.SilencePeriod} seconds of silence");
                    return _optionsService.Options.AutoRestartAfterSilence ? eStopModes.CopyRestart : eStopModes.JustStop;
                }
                else if (_ElapsedTime.TotalSeconds >= _silenceSecToRestart && _optionsService.Options.AutoRestartAfterSilence)
                {
                    // It's been no sound?
                    StopCommand($"[No audio detected] Automatically stopped recording having detected {_silenceSecToRestart} seconds of silence");
                    return _optionsService.Options.AutoRestartAfterSilence ? eStopModes.DeleteRestart : eStopModes.JustStop;
                }
            }
            return eStopModes.Nothing;
        }

        private eStopModes StopIfChunkDetected()
        {
            int average = GetInputVolumeAverage(_volumeAverageForChunk, DateTime.Now, _optionsService.Options.ChunkDetectionSpan);
            //Log.Logger.Information($"{_optionsService.Options.ChunkDetectionSpan}ms ave = {average}");
            if (average <= _chunkDetectionVolumeThreshold && _ElapsedTime.TotalSeconds >= _chunkMinimumSecond)
            {
                // 
                StopCommand("chunk detected");
                if (HasAudioDetected())
                {
                    return eStopModes.CopyRestart;
                }
                else
                {
                    return eStopModes.DeleteRestart;
                }
            }
            return eStopModes.Nothing;
        }

        private int GetInputVolumeAverage(Queue<VolumeSample> averageQueue, DateTime currentTime, int lastMiliSec)
        {
            while (averageQueue.Count > 0 && averageQueue.Peek().Timestamp < currentTime.AddMilliseconds(lastMiliSec * -1))
            {
                _ = averageQueue.Dequeue();
            }
            averageQueue.Enqueue(new VolumeSample { VolumeLevel = VolumeLevelAsPercentage, Timestamp = currentTime });

            int average = 0;
            averageQueue.ForEach(v => average += v.VolumeLevel);
            average /= averageQueue.Count;
            return average;
        }

        private bool HasAudioDetected()
        {
            return _maxAverageVolumeLevel > _validInputLevel;
        }

        private void StopCommand(string logMsg)
        {
            RecordingStatus = RecordingStatus.StopRequested;  // Prevent threading from requesting multiple stops
            Log.Logger.Information(logMsg);
            StopRecordingCommand.Execute(null);
        }

        //private void AutoStopRecordingAfterSilence()
        //{
        //    Log.Logger.Information(
        //        "Automatically stopped recording after {Limit} seconds of silence",
        //        _optionsService.Options.MaxSilenceTimeSeconds);

        //    StopRecordingCommand.Execute(null);
        //}

        private void AutoStopRecordingAtLimit()
        {
            Log.Logger.Information(
                "Automatically stopped recording having reached the {Limit} second limit",
                _optionsService.Options.MaxRecordingTimeSeconds);

            StopRecordingCommand.Execute(null);
        }

        private void AudioStopRequestedHandler(object? sender, EventArgs e)
        {
            Log.Logger.Information("Stop requested");
            RecordingStatus = RecordingStatus.StopRequested;
        }

        private void AudioStoppedHandler(object? sender, EventArgs e)
        {
            Log.Logger.Information("Stopped recording");
            RecordingStatus = RecordingStatus.NotRecording;
            VolumeLevelAsPercentage = 0;
            _maxAverageVolumeLevel= 0;
            _stopwatch.Stop();
        }

        private void StopCompleteHandler(object? sender, RecordingStatusChangeEventArgs e)
        {
            // AutoRestart
            switch (_eStopMode)
            {
                case eStopModes.CopyRestart:
                    restartRecording();
                    break;
                case eStopModes.DeleteRestart:
                    if (File.Exists(e.FinalRecordingPath))
                    {
                        File.Delete(e.FinalRecordingPath);
                        Log.Logger.Information($"Silence file '{Path.GetFileName(e.FinalRecordingPath)}' is deleted");
                    }
                    restartRecording();
                    break;
            }

            void restartRecording()
            {
                _eStopMode = eStopModes.JustStop;
                Log.Logger.Information("Restart recording after silence detected");
                StartRecordingCommand.Execute(null);
            }
        }

        private void AudioStartedHandler(object? sender, EventArgs e)
        {
            Log.Logger.Information("Started recording");
            RecordingStatus = RecordingStatus.Recording;
        }

        private void StartRecording()
        {
            try
            {
                ClearErrorMsg();
                Log.Logger.Information("Start requested");

                var recordingDate = DateTime.Today;
                var candidateFile = _destinationService.GetRecordingFileCandidate(
                    _optionsService, recordingDate, _commandLineService.OptionsIdentifier);

                CheckDiskSpace(candidateFile);

                _audioService.StartRecording(candidateFile, _optionsService);
                _stopwatch.Restart();
            }
            catch (Exception ex)
            {
                ErrorMsg = Properties.Resources.ERROR_START;
                Log.Logger.Error(ex, ErrorMsg);
            }
        }

        private void CheckDiskSpace(RecordingCandidate candidate)
        {
            CheckDiskSpace(candidate.TempPath);
            CheckDiskSpace(candidate.FinalPath);
        }

        private void CheckDiskSpace(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            var folder = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            if (FileUtils.DriveFreeBytes(folder, out ulong bytesFree) && 
                bytesFree < _safeMinBytesFree)
            {
                // "Insufficient free space to record"
                throw new IOException(Properties.Resources.INSUFFICIENT_FREE_SPACE);
            }
        }

        private void StopRecording()
        {
            try
            {
                ClearErrorMsg();
                _volumeAverageForSilence.Clear();
                _volumeAverageForChunk.Clear();
                _audioService.StopRecording(_optionsService.Options?.FadeOut ?? false);
            }
            catch (Exception ex)
            {
                ErrorMsg = Properties.Resources.ERROR_STOP;
                Log.Logger.Error(ex, ErrorMsg);
            }
        }

        private void ClearErrorMsg()
        {
            ErrorMsg = null;
        }

        /// <summary>
        /// Show brief animation
        /// </summary>
        private void DoSplash()
        {
            // "Splash" is a graphical effect rendered in the volume meter
            // when the Recording page loads for the first time. It's designed
            // to show that all is working, and OnlyR is ready to record!
            if (_splashTimer == null)
            {
                _splashTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
                _splashTimer.Tick += SplashTimerTick;
            }

            VolumeLevelAsPercentage = 100;
            _splashTimer.Start();
        }

        private void SplashTimerTick(object? sender, EventArgs e)
        {
            if (IsNotRecording)
            {
                VolumeLevelAsPercentage -= 6;
                if (VolumeLevelAsPercentage <= 0)
                {
                    _splashTimer?.Stop();
                }
            }
            else
            {
                _splashTimer?.Stop();
            }
        }

        private void ShowRecordings()
        {
            var folder = FindSuitableRecordingFolderToShow();
            var psi = new ProcessStartInfo
            {
                FileName = folder, 
                UseShellExecute = true
            };

            Process.Start(psi);
        }

        private string FindSuitableRecordingFolderToShow()
        {
            return FileUtils.FindSuitableRecordingFolderToShow(
                _commandLineService.OptionsIdentifier,
                _optionsService.Options?.DestinationFolder);
        }

        private void SaveToRemovableDrives()
        {
            IsCopying = true;
            
            Task.Run(() =>
            {
                try
                {
                    _copyRecordingsService.Copy(_removableDrives.Keys.ToArray());
                    _snackbarService.Enqueue(
                        Properties.Resources.COPIED,
                        Properties.Resources.OK,
                        _ => { },
                        null,
                        promote: false,
                        neverConsiderToBeDuplicate: true);
                }
                catch (NoRecordingsException ex)
                {
                    _snackbarService.EnqueueWithOk(ex.Message);
                }
                catch (AggregateException ex)
                {
                    foreach (var innerException in ex.InnerExceptions)
                    {
                        if (innerException is NoSpaceException exception)
                        {
                            _snackbarService.EnqueueWithOk(exception.Message);
                        }
                        else
                        {
                            _snackbarService.EnqueueWithOk(Properties.Resources.UNKNOWN_COPY_ERROR);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, Properties.Resources.UNKNOWN_COPY_ERROR);
                    _snackbarService.EnqueueWithOk(Properties.Resources.UNKNOWN_COPY_ERROR);
                }
                finally
                {
                    Application.Current.Dispatcher.Invoke(() => IsCopying = false);
                }
            });
        }

        private void OnRemovableDriveMessage(object recipient, RemovableDriveMessage message)
        {
            if (message.Added)
            {
                _removableDrives[message.DriveLetter] = DateTime.UtcNow;
            }
            else
            {
                _removableDrives.TryRemove(message.DriveLetter, out var _);
            }

            OnPropertyChanged(nameof(IsSaveVisible));
            OnPropertyChanged(nameof(SaveHint));
        }
    }
}
