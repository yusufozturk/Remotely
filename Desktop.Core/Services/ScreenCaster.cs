﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Remotely.Desktop.Core.Interfaces;
using System.Diagnostics;
using System.Drawing.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Remotely.Desktop.Core.Utilities;
using Remotely.Shared.Utilities;
using System.Collections.Concurrent;
using Remotely.Desktop.Core.Enums;
using Remotely.Shared.Models;
using Remotely.Shared.Win32;
using System.Drawing;
using System.Threading;

namespace Remotely.Desktop.Core.Services
{
    public class ScreenCaster : IScreenCaster
    {
        public ScreenCaster(Conductor conductor, 
            ICursorIconWatcher cursorIconWatcher,
            ISessionIndicator sessionIndicator,
            IShutdownService shutdownService)
        {
            Conductor = conductor;
            CursorIconWatcher = cursorIconWatcher;
            SessionIndicator = sessionIndicator;
            ShutdownService = shutdownService;
        }

        private Conductor Conductor { get; }
        private ICursorIconWatcher CursorIconWatcher { get; }
        private ISessionIndicator SessionIndicator { get; }
        private IShutdownService ShutdownService { get; }

        public async Task BeginScreenCasting(ScreenCastRequest screenCastRequest)
        {
            var mode = AppMode.Unattended;

            try
            {
                Bitmap currentFrame = null;
                Bitmap previousFrame = null;
                byte[] encodedImageBytes;
                var fpsQueue = new Queue<DateTimeOffset>();
                mode = Conductor.Mode;
                var viewer = ServiceContainer.Instance.GetRequiredService<Viewer>();
                viewer.Name = screenCastRequest.RequesterName;
                viewer.ViewerConnectionID = screenCastRequest.ViewerID;

                Logger.Write($"Starting screen cast.  Requester: {viewer.Name}. Viewer ID: {viewer.ViewerConnectionID}.  App Mode: {mode}");

                Conductor.Viewers.AddOrUpdate(viewer.ViewerConnectionID, viewer, (id, v) => viewer);

                if (mode == AppMode.Normal)
                {
                    Conductor.InvokeViewerAdded(viewer);
                }

                if (mode == AppMode.Unattended && screenCastRequest.NotifyUser)
                {
                    SessionIndicator.Show();
                }

                if (EnvironmentHelper.IsWindows)
                {
                    Win32Interop.SwitchToInputDesktop();
                    await viewer.InitializeWebRtc();
                }

                await viewer.SendMachineName(Environment.MachineName);

                await viewer.SendScreenData(
                       viewer.Capturer.SelectedScreen,
                       viewer.Capturer.GetDisplayNames().ToArray());

                await viewer.SendScreenSize(viewer.Capturer.CurrentScreenBounds.Width,
                    viewer.Capturer.CurrentScreenBounds.Height);

                await viewer.SendCursorChange(CursorIconWatcher.GetCurrentCursor());

                await viewer.SendWindowsSessions();

                viewer.Capturer.ScreenChanged += async (sender, bounds) =>
                {
                    await viewer.SendScreenSize(bounds.Width, bounds.Height);
                };

                using (var initialFrame = viewer.Capturer.GetNextFrame())
                {
                    await viewer.SendScreenCapture(
                         ImageUtils.EncodeBitmap(initialFrame, viewer.EncoderParams),
                         viewer.Capturer.CurrentScreenBounds.Left,
                         viewer.Capturer.CurrentScreenBounds.Top,
                         viewer.Capturer.CurrentScreenBounds.Width,
                         viewer.Capturer.CurrentScreenBounds.Height);
                }

 

                while (!viewer.DisconnectRequested && viewer.IsConnected)
                {
                    try
                    {
                        if (viewer.IsUsingWebRtcVideo)
                        {
                            Thread.Sleep(100);
                            continue;
                        }
                        if (viewer.IsStalled)
                        {
                            // Viewer isn't responding.  Abort sending.
                            break;
                        }

                        if (EnvironmentHelper.IsDebug)
                        {
                            while (fpsQueue.Any() && DateTimeOffset.Now - fpsQueue.Peek() > TimeSpan.FromSeconds(1))
                            {
                                fpsQueue.Dequeue();
                            }
                            fpsQueue.Enqueue(DateTimeOffset.Now);
                            Debug.WriteLine($"Capture FPS: {fpsQueue.Count}");
                        }

                        viewer.ThrottleIfNeeded();

                        if (currentFrame != null)
                        {
                            previousFrame?.Dispose();
                            previousFrame = (Bitmap)currentFrame.Clone();
                        }

                        currentFrame?.Dispose();
                        currentFrame = viewer.Capturer.GetNextFrame();

                        var diffArea = ImageUtils.GetDiffArea(currentFrame, previousFrame, viewer.Capturer.CaptureFullscreen);

                        if (diffArea.IsEmpty)
                        {
                            continue;
                        }

                        using var newImage = currentFrame.Clone(diffArea, PixelFormat.Format32bppArgb);
                        if (viewer.Capturer.CaptureFullscreen)
                        {
                            viewer.Capturer.CaptureFullscreen = false;
                        }

                        encodedImageBytes = ImageUtils.EncodeBitmap(newImage, viewer.EncoderParams);

                        if (encodedImageBytes?.Length > 0)
                        {
                            await viewer.SendScreenCapture(encodedImageBytes, diffArea.Left, diffArea.Top, diffArea.Width, diffArea.Height);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Write(ex);
                    }
                }

                Logger.Write($"Ended screen cast.  Requester: {viewer.Name}. Viewer ID: {viewer.ViewerConnectionID}.");
                Conductor.Viewers.TryRemove(viewer.ViewerConnectionID, out _);
                viewer.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
            }
            finally
            {
                // Close if no one is viewing.
                if (Conductor.Viewers.Count == 0 && mode == AppMode.Unattended)
                {
                    await ShutdownService.Shutdown();
                }
            }
        }
    }
}
