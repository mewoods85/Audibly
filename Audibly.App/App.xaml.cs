// Author: rstewa · https://github.com/rstewa
// Updated: 07/30/2025

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Audibly.App.Extensions;
using Audibly.App.Helpers;
using Audibly.App.Services;
using Audibly.App.ViewModels;
using Audibly.Models;
using Audibly.Models.v1;
using Audibly.Repository.Interfaces;
using Audibly.Repository.Sql;
using CommunityToolkit.WinUI;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using Sentry;
using WinRT.Interop;
using Constants = Audibly.App.Helpers.Constants;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;

namespace Audibly.App;

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static Win32WindowHelper win32WindowHelper;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>
    ///     Initializes the singleton application object.  This is the first line of authored code
    ///     executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
#if !DEBUG
    var dsn = Helpers.Sentry.Dsn;

    // Only initialize Sentry if we actually have a valid DSN
    if (!string.IsNullOrWhiteSpace(dsn) &&
        Uri.IsWellFormedUriString(dsn, UriKind.Absolute))
    {
        SentrySdk.Init(options =>
        {
            options.Dsn = dsn;
            options.AutoSessionTracking = true;
            options.SampleRate = 0.25f;
            options.TracesSampleRate = 0.25;
            options.IsGlobalModeEnabled = true;
            options.ProfilesSampleRate = 0.25;
            options.Environment = "production";
        });
    }
#endif

        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }


    /// <summary>
    ///     Gets main App Window
    /// </summary>
    public static Window Window { get; private set; }

    /// <summary>
    ///     Gets the app-wide MainViewModel singleton instance.
    /// </summary>
    public static MainViewModel ViewModel { get; } =
        new(new FileImportService(), new AppDataService(),
            new LoggingService(ApplicationData.Current.LocalFolder.Path + @"\Audibly.log"), new FileDialogService());

    /// <summary>
    ///     Gets the app-wide PlayerViewModel singleton instance.
    /// </summary>
    public static PlayerViewModel PlayerViewModel { get; } = new();

    /// <summary>
    ///     Pipeline for interacting with backend service or database.
    /// </summary>
    public static IAudiblyRepository Repository { get; private set; }

    /// <summary>
    ///     Gets the root frame of the app. This contains the nav view and the player page
    /// </summary>
    public static Frame? RootFrame { get; private set; }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ViewModel.LoggingService.LogError(e.Exception, true);
    }

    /// <summary>
    ///     Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // If this is the first instance launched, then register it as the "main" instance.
        // If this isn't the first instance launched, then "main" will already be registered,
        // so retrieve it.
        var mainInstance = AppInstance.FindOrRegisterForKey("main");
        mainInstance.Activated += OnAppInstanceActivated;

        // If the instance that's executing the OnLaunched handler right now
        // isn't the "main" instance.
        if (!mainInstance.IsCurrent)
        {
            // Redirect the activation (and args) to the "main" instance, and exit.
            var activatedEventArgs =
                AppInstance.GetCurrent().GetActivatedEventArgs();
            await mainInstance.RedirectActivationToAsync(activatedEventArgs);
            Process.GetCurrentProcess().Kill();
            return;
        }

        if (MicaController.IsSupported())
            Current.Resources["AppShellBackgroundBrush"] = new SolidColorBrush(Colors.Transparent);

        Window = WindowHelper.CreateWindow("MainWindow");

        var appWindow = WindowHelper.GetAppWindow(Window);
        appWindow.Closing += async (_, _) =>
        {
            if (PlayerViewModel.NowPlaying != null) await PlayerViewModel.NowPlaying.SaveAsync();
            PlayerViewModel.Dispose();
            WindowHelper.CloseAll();
        };
        appWindow.Title = "Audibly — Audiobook Player";
        appWindow.SetIcon("Assets/logo.ico");

        win32WindowHelper = new Win32WindowHelper(Window);
        win32WindowHelper.SetWindowMinMaxSize(new Win32WindowHelper.POINT { x = 940, y = 640 });

        UseSqlite();

        RootFrame = Window.Content as Frame;

        if (RootFrame == null)
        {
            RootFrame = new Frame();
            RootFrame.NavigationFailed += OnNavigationFailed;
            Window.Content = RootFrame;
        }

        if (RootFrame.Content == null) RootFrame.Navigate(typeof(AppShell), args.Arguments);

        (Window as MainWindow)?.TrySetSystemBackdrop();

        Window.CustomizeWindow(-1, -1, true, true, true, true, true, true);

        ThemeHelper.Initialize();

        // handle file activation
        // got this from Andrew KeepCoding's answer here: https://stackoverflow.com/questions/76650127/how-to-handle-activation-through-files-in-winui-3-packaged
        var appActivationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (appActivationArguments.Kind is ExtendedActivationKind.File &&
            appActivationArguments.Data is IFileActivatedEventArgs fileActivatedEventArgs &&
            fileActivatedEventArgs.Files.FirstOrDefault() is IStorageFile storageFile)
            await _dispatcherQueue.EnqueueAsync(() => HandleFileActivation(storageFile));

        Window.Activate();
    }

    private async void HandleFileActivation(IStorageFile storageFile, bool onAppInstanceActivated = false)
    {
        ViewModel.LoggingService.Log($"File activated: {storageFile.Path}");

        try
        {
            if (onAppInstanceActivated)
            {
                // if the app is already running and a file is opened, then we need to handle it differently
                await _dispatcherQueue.EnqueueAsync(() => HandleFileActivationOnAppInstanceActivated(storageFile));
                return;
            }

            // check the database for the audiobook
            var audiobook = await Repository.Audiobooks.GetByFilePathAsync(storageFile.Path);

            // if filepath doesn't match, then we need its metadata to check if it's in the database
            if (audiobook == null)
            {
                var metadata = storageFile.GetAudiobookSearchParameters();
                audiobook = await Repository.Audiobooks.GetByTitleAuthorComposerAsync(metadata.Title, metadata.Author,
                    metadata.Composer);

                // if the audiobook is not in the database, then we need to import it
                if (audiobook == null)
                {
                    await ViewModel.ImportAudiobookFromFileActivationAsync(storageFile.Path, false);
                    return;
                }
            }

            // if the audiobook is already playing, then we don't need to do anything
            if (audiobook.IsNowPlaying) return;

            // we need to get the currently playing audiobook and set it to not playing
            var nowPlayingAudiobook = await Repository.Audiobooks.GetNowPlayingAsync();
            if (nowPlayingAudiobook != null)
            {
                nowPlayingAudiobook.IsNowPlaying = false;
                await Repository.Audiobooks.UpsertAsync(nowPlayingAudiobook);
            }

            // set file activated audiobook to now playing
            audiobook.IsNowPlaying = true;
            await Repository.Audiobooks.UpsertAsync(audiobook);
        }
        catch (Exception e)
        {
            ViewModel.EnqueueNotification(new Notification
            {
                Message = "An error occurred while trying to open the file.",
                Severity = InfoBarSeverity.Error
            });
            ViewModel.LoggingService.LogError(e, true);

            if (onAppInstanceActivated)
                await DialogService.ShowErrorDialogAsync("File Activation Error", e.Message);
            else
                ViewModel.FileActivationError = e.Message;
        }
    }

    private async void HandleFileActivationOnAppInstanceActivated(IStorageFile storageFile)
    {
        // need to refresh the audiobook list in case any filters or searches have been applied
        await ViewModel.GetAudiobookListAsync();

        var searchParameters = storageFile.GetAudiobookSearchParameters();
        var audiobook = ViewModel.Audiobooks.FirstOrDefault(a => a.Title == searchParameters.Title &&
                                                                 a.Author == searchParameters.Author &&
                                                                 a.Narrator == searchParameters.Composer);

        // set the current position
        if (audiobook == null)
        {
            await ViewModel.ImportAudiobookFromFileActivationAsync(storageFile.Path, false);
            return;
        }

        // if the audiobook is already playing, then we don't need to do anything
        if (audiobook.IsNowPlaying) return;

        // if the audiobook is not playing, then we need to set the current position
        await PlayerViewModel.OpenAudiobook(audiobook);
    }

    // note: this is only called when audibly is already running and a file is opened
    private async void OnAppInstanceActivated(object? sender, AppActivationArguments e)
    {
        var mainInstance = AppInstance.FindOrRegisterForKey("main");

        if (e.Kind is ExtendedActivationKind.File && e.Data is IFileActivatedEventArgs fileActivatedEventArgs &&
            fileActivatedEventArgs.Files.FirstOrDefault() is IStorageFile storageFile)
        {
            await _dispatcherQueue.EnqueueAsync(() => HandleFileActivation(storageFile, true));

            // Bring the window to the foreground... first get the window handle...
            var hwnd = (HWND)WindowNative.GetWindowHandle(Window);

            // Restore window if minimized... requires Microsoft.Windows.CsWin32 NuGet package and a NativeMethods.txt file with ShowWindow method
            Windows.Win32.PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

            // And call SetForegroundWindow... requires Microsoft.Windows.CsWin32 NuGet package and a NativeMethods.txt file with SetForegroundWindow method
            Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
        }
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        ViewModel.LoggingService.Log(e.Exception.Message);
    }

    /// <summary>
    ///     Configures the app to use the Sqlite data source. If no existing Sqlite database exists,
    ///     loads a demo database filled with fake data so the app has content.
    /// </summary>

    private static void UseSqlite()
    {
        // Use the same path that the app has been using
        var dbPath = ApplicationData.Current.LocalFolder.Path + @"\Audibly.db";

        // OPTIONAL but recommended for a clean local dev start:
        // if there's a broken / half-created DB, delete it.
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        var dbOptions = new DbContextOptionsBuilder<AudiblyContext>()
            .UseSqlite("Data Source=" + dbPath)
            .Options;

        try
        {
            // Don't use migrations here; just ensure the schema matches the current model.
            using (var context = new AudiblyContext(dbOptions))
            {
                // Creates the DB and all tables if they don't exist
                context.Database.EnsureCreated();
            }

            // Hook up the repository to this context configuration
            Repository = new SqlAudiblyRepository(dbOptions);
        }
        catch (Exception e)
        {
            // If anything goes wrong, log it and still initialize the repository
            ViewModel.LoggingService.LogError(e, true);
            Repository = new SqlAudiblyRepository(dbOptions);
        }
    }


    public static void RestartApp()
    {
        var restartError = AppInstance.Restart("themeChanged");

        switch (restartError)
        {
            case AppRestartFailureReason.RestartPending:
                ViewModel.EnqueueNotification(new Notification
                {
                    Message = "Another restart is currently pending.",
                    Severity = InfoBarSeverity.Error
                });
                break;
            case AppRestartFailureReason.InvalidUser:
                ViewModel.EnqueueNotification(new Notification
                {
                    Message = "Restart failed: Invalid user.",
                    Severity = InfoBarSeverity.Error
                });
                break;
            case AppRestartFailureReason.Other:
                ViewModel.EnqueueNotification(new Notification
                {
                    Message = "Restart failed: Unknown error.",
                    Severity = InfoBarSeverity.Error
                });
                break;
        }
    }

    public static TEnum GetEnum<TEnum>(string text) where TEnum : struct
    {
        if (!typeof(TEnum).GetTypeInfo().IsEnum)
            throw new InvalidOperationException("Generic parameter 'TEnum' must be an enum.");
        return (TEnum)Enum.Parse(typeof(TEnum), text);
    }
}