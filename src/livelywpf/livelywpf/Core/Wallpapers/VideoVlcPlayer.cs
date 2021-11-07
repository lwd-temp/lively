﻿using livelywpf.Core.API;
using livelywpf.Helpers;
using livelywpf.Helpers.Pinvoke;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using livelywpf.Models;

namespace livelywpf.Core.Wallpapers
{
    //Ref: 
    //https://github.com/rocksdanister/lively/discussions/342
    //https://wiki.videolan.org/documentation:modules/rc/
    public class VideoVlcPlayer : IWallpaper
    {
        //private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        public event EventHandler<WindowInitializedArgs> WindowInitialized;
        private IntPtr hwnd;
        private readonly Process _process;
        private readonly ILibraryModel model;
        private ILivelyScreen display;
        private readonly CancellationTokenSource ctsProcessWait = new CancellationTokenSource();
        private Task processWaitTask;
        private readonly int timeOut;

        public bool IsLoaded => hwnd != IntPtr.Zero;

        public WallpaperType Category => model.LivelyInfo.Type;

        public ILibraryModel Model => model;

        public IntPtr Handle => hwnd;

        public IntPtr InputHandle => IntPtr.Zero;

        public Process Proc => _process;

        public ILivelyScreen Screen { get => display; set => display = value; }

        public string LivelyPropertyCopyPath => null;

        public VideoVlcPlayer(string path, ILibraryModel model, ILivelyScreen display, WallpaperScaler scaler = WallpaperScaler.fill)
        {
            var scalerArg = scaler switch
            {
                WallpaperScaler.none => "--no-autoscale ",
                WallpaperScaler.fill => "--aspect-ratio=" + display.Bounds.Width + ":" + display.Bounds.Height,
                WallpaperScaler.uniform => "--autoscale",
                WallpaperScaler.uniformFill => "--crop=" + display.Bounds.Width + ":" + display.Bounds.Height,
                _ => "--autoscale",
            };

            StringBuilder cmdArgs = new StringBuilder();
            //--no-video-title.
            cmdArgs.Append("--no-osd ");
            //video stretch algorithm.
            cmdArgs.Append(scalerArg + " ");
            //hide menus and controls.
            cmdArgs.Append("--qt-minimal-view ");
            //do not create system-tray icon.
            cmdArgs.Append("--no-qt-system-tray ");
            //prevent player window resizing to video size.
            cmdArgs.Append("--no-qt-video-autoresize ");
            //allow screensaver.
            cmdArgs.Append("--no-disable-screensaver ");
            //open window at (-9999,0), not working without: --no-embedded-video
            cmdArgs.Append("--video-x=-9999 --video-y=0 ");
            //gpu decode preference.
            cmdArgs.Append(Program.SettingsVM.Settings.VideoPlayerHwAccel ? "--avcodec-hw=any " : "--avcodec-hw=none ");
            //media file path.
            cmdArgs.Append("\"" + path + "\"");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "vlc", "vlc.exe"),
                UseShellExecute = false,
                WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "vlc"),
                Arguments = cmdArgs.ToString(),
            };

            Process _process = new Process()
            {
                EnableRaisingEvents = true,
                StartInfo = start,
            };

            this._process = _process;
            this.model = model;
            this.display = display;
            this.timeOut = 20000;
        }

        public async void Close()
        {
            TaskProcessWaitCancel();
            while (!IsProcessWaitDone())
            {
                await Task.Delay(1);
            }

            //Not reliable, app may refuse to close(open dialogue window.. etc)
            //Proc.CloseMainWindow();
            Terminate();
        }

        public WallpaperType GetWallpaperType()
        {
            return model.LivelyInfo.Type;
        }

        public void Pause()
        {
            //todo
        }

        public void Play()
        {
            //todo
        }

        public void SendMessage(string msg)
        {
            //todo
        }

        public void SetPlaybackPos(float pos, PlaybackPosType type)
        {
            //todo
        }

        public void SetVolume(int volume)
        {
            //todo
        }

        public async void Show()
        {
            if (_process != null)
            {
                try
                {
                    _process.Exited += Proc_Exited;
                    _process.Start();
                    processWaitTask = Task.Run(() => hwnd = WaitForProcesWindow().Result, ctsProcessWait.Token);
                    await processWaitTask;
                    if (hwnd.Equals(IntPtr.Zero))
                    {
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                        {
                            Success = false,
                            Error = new Exception(Properties.Resources.LivelyExceptionGeneral),
                            Msg = "Process window handle is zero."
                        });
                    }
                    else
                    {
                        WindowOperations.BorderlessWinStyle(hwnd);
                        WindowOperations.RemoveWindowFromTaskbar(hwnd);
                        //Program ready!
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                        {
                            Success = true,
                            Error = null,
                            Msg = null
                        });
                        //todo: Restore livelyproperties.json settings here..
                    }
                }
                catch (OperationCanceledException e1)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e1,
                        Msg = "Program wallpaper terminated early/user cancel."
                    });
                }
                catch (InvalidOperationException e2)
                {
                    //No GUI, program failed to enter idle state.
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e2,
                        Msg = "Program wallpaper crashed/closed already!"
                    });
                }
                catch (Exception e3)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e3,
                        Msg = ":("
                    });
                }
            }
        }

        private void Proc_Exited(object sender, EventArgs e)
        {
            _process?.Dispose();
            SetupDesktop.RefreshDesktop();
        }

        #region process task

        /// <summary>
        /// Function to search for window of spawned program.
        /// </summary>
        private async Task<IntPtr> WaitForProcesWindow()
        {
            if (_process == null)
            {
                return IntPtr.Zero;
            }

            _process.Refresh();
            //waiting for program messageloop to be ready (GUI is not guaranteed to be ready.)
            while (_process.WaitForInputIdle(-1) != true)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
            }

            IntPtr wHWND = IntPtr.Zero;
            //Find process window.
            for (int i = 0; i < timeOut && _process.HasExited == false; i++)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
                if (!IntPtr.Equals((wHWND = GetProcessWindow(_process, true)), IntPtr.Zero))
                    break;
                await Task.Delay(1);
            }
            return wHWND;
        }

        /// <summary>
        /// Retrieve window handle of process.
        /// </summary>
        /// <param name="proc">Process to search for.</param>
        /// <param name="win32Search">Use win32 method to find window.</param>
        /// <returns></returns>
        private IntPtr GetProcessWindow(Process proc, bool win32Search = false)
        {
            if (this._process == null)
                return IntPtr.Zero;

            if (win32Search)
            {
                return FindWindowByProcessId(proc.Id);
            }
            else
            {
                proc.Refresh();
                //Issue(.net core) MainWindowHandle zero: https://github.com/dotnet/runtime/issues/32690
                return proc.MainWindowHandle;
            }
        }

        private IntPtr FindWindowByProcessId(int pid)
        {
            IntPtr HWND = IntPtr.Zero;
            NativeMethods.EnumWindows(new NativeMethods.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                _ = NativeMethods.GetWindowThreadProcessId(tophandle, out int cur_pid);
                if (cur_pid == pid)
                {
                    if (NativeMethods.IsWindowVisible(tophandle))
                    {
                        HWND = tophandle;
                        return false;
                    }
                }

                return true;
            }), IntPtr.Zero);

            return HWND;
        }

        /// <summary>
        /// Cancel waiting for pgm wp window to be ready.
        /// </summary>
        private void TaskProcessWaitCancel()
        {
            if (ctsProcessWait == null)
                return;

            ctsProcessWait.Cancel();
            ctsProcessWait.Dispose();
        }

        /// <summary>
        /// Check if started pgm ready(GUI window started).
        /// </summary>
        /// <returns>true: process ready/halted, false: process still starting.</returns>
        private bool IsProcessWaitDone()
        {
            var task = processWaitTask;
            if (task != null)
            {
                if ((task.IsCompleted == false
                || task.Status == TaskStatus.Running
                || task.Status == TaskStatus.WaitingToRun
                || task.Status == TaskStatus.WaitingForActivation))
                {
                    return false;
                }
                return true;
            }
            return true;
        }

        #endregion process task


        public void Stop()
        {
            //todo
        }

        public void Terminate()
        {
            try
            {
                _process.Kill();
            }
            catch { }
            SetupDesktop.RefreshDesktop();
        }

        public Task ScreenCapture(string filePath)
        {
            throw new NotImplementedException();
        }

        public void SendMessage(IpcMessage obj)
        {
            //todo
        }
    }
}
