using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Watching;

internal sealed partial class SessionDirector
{
    private readonly Action<string, object> controlPublish;
    private readonly Queue<RecordingCommand> commands = new();
    private ManualSession? manualSession;
    private uint suppressedId;
    private bool stopConfirmation;
    private bool controlInitialized;
    private bool lastManualMode;
    private string? controlError;
    private RecordingControlState? lastControlState;
    private bool controlBusy;

    public void Control(RecordingCommand command)
    {
        lock (gate)
        {
            if (controlBusy && command.Action is "start" or "toggle" or "requestStop" or "stop" or "recover") return;
            commands.Enqueue(command);
        }
        wake.Set();
    }

    private void HandleControl()
    {
        var manual = common?.ManualControl == true;
        if (suppressedId != 0 && !Alive(suppressedId)) suppressedId = 0;
        if (manual && session is not null)
        {
            DropSession();
            folder = null;
            folderApp = null;
            targetApp = null;
        }
        if (!manual && manualSession is not null) StopManual(false);
        if (!controlInitialized || manual != lastManualMode)
        {
            controlInitialized = true;
            lastManualMode = manual;
            stopConfirmation = false;
            controlError = null;
            PublishControl(true);
        }
        while (true)
        {
            RecordingCommand command;
            lock (gate)
            {
                if (!commands.TryDequeue(out command!)) break;
            }
            try
            {
                switch (command.Action)
                {
                    case "sources":
                        controlPublish(PipeMessages.CaptureSources, new CaptureSourceList(CaptureSources.List()));
                        PublishControl(true);
                        break;
                    case "toggle":
                        if (manualSession is not null || session is not null) RequestStop();
                        else StartSelected();
                        break;
                    case "start":
                        StartSelected();
                        break;
                    case "requestStop":
                        RequestStop();
                        break;
                    case "cancelStop":
                        stopConfirmation = false;
                        break;
                    case "stop":
                        if (!stopConfirmation && manualSession?.Continuous != true) break;
                        if (manualSession is not null) StopManual(command.SaveBuffer);
                        else StopAutomatic(command.SaveBuffer);
                        stopConfirmation = false;
                        break;
                    case "recover":
                        if (manualSession is not null || session is not null) break;
                        PublishControl(false, "finalizing");
                        var clip = RecordingRecovery.Recover(settings.ClipsRoot, command.RecoveryId ?? string.Empty);
                        controlPublish(PipeMessages.ClipSaved, clip);
                        controlError = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                report($"ручная запись: {ex.Message}");
                controlError = Strings.Code("Recording.Error", ex.Message);
                if (command.Action == "sources")
                    controlPublish(PipeMessages.CaptureSources, new CaptureSourceList([], controlError));
            }
            PublishControl(true);
        }
        if (!manual) PublishControl();
    }

    private void StartSelected()
    {
        if (session is not null || manualSession is not null) return;
        controlError = null;
        stopConfirmation = false;
        if (common?.ManualControl != true)
        {
            suppressedId = 0;
            return;
        }
        var selected = common.Manual?.SourceId;
        var source = CaptureSources.List().SingleOrDefault(item => item.Id == selected)
            ?? throw new InvalidOperationException(Strings.Get("Recording.SourceMissing"));
        var group = source.Kind == CaptureSourceKind.Monitor ? "Desktop"
            : apps.Match(source.Application, () => source.ExecutablePath)?.Title
                ?? Path.GetFileNameWithoutExtension(source.Application);
        PublishControl(false, "starting");
        Diagnostics.Log.Report($"ручной захват: {source.Kind}, {source.Title}, {source.Width}x{source.Height}");
        var started = new ManualSession(source, group, common, video, saved, trace, health, shown);
        lock (gate) manualSession = started;
        state(new RecordingState(true, group, started.Folder, false, common.Manual?.BufferSeconds ?? common.BufferSeconds,
            common.Hotkey, Strings.Code(source.Kind == CaptureSourceKind.Monitor ? "Recording.Monitor" : "Recording.Window"),
            false, started.Continuous));
    }

    private void RequestStop()
    {
        if (manualSession?.Continuous == true) StopManual(false);
        else if (manualSession is not null || session is not null) stopConfirmation = true;
    }

    private void StopAutomatic(bool save)
    {
        if (session is null && targetApp is null) return;
        suppressedId = targetId;
        PublishControl(false, "finalizing");
        if (save) session?.Trigger();
        DropSession();
        targetApp = null;
        folder = null;
        folderApp = null;
        rebind(null);
    }

    private void StopManual(bool save)
    {
        ManualSession? closing;
        lock (gate)
        {
            closing = manualSession;
            manualSession = null;
        }
        if (closing is null) return;
        PublishControl(false, "finalizing");
        try
        {
            if (closing.Stop(save) is { } clip) controlPublish(PipeMessages.ClipSaved, clip);
            else if (closing.Continuous && closing.Seconds <= 0) controlError = Strings.Code("Recording.NoFrames");
        }
        catch (Exception ex)
        {
            controlError = Strings.Code("Recording.Error", ex.Message);
            report($"ручная запись остановлена с ошибкой: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
            state(new RecordingState(false, null, null));
            stopConfirmation = false;
        }
    }

    private void TickManual()
    {
        detector.Forget();
        if (manualSession is { } current)
        {
            try
            {
                if (current.Failure is { } error) throw error;
                if (!CaptureSources.List().Any(item => item.Id == current.Source.Id && item.Handle == current.Source.Handle))
                    throw new IOException(Strings.Get("Recording.SourceClosed"));
                if (!current.Continuous) memory(current.Bytes);
            }
            catch (Exception ex)
            {
                controlError = Strings.Code("Recording.Error", ex.Message);
                StopManual(false);
            }
        }
        PublishControl();
    }

    private void PublishControl(bool recoveries = false, string? stage = null)
    {
        lock (gate) controlBusy = stage is "starting" or "finalizing";
        var current = manualSession;
        var active = current is not null || session is not null;
        var next = new RecordingControlState(active, common?.ManualControl == true, current?.Continuous ?? false,
            stage ?? (active ? "recording" : "idle"), current?.Source,
            current?.Seconds ?? 0, current?.Bytes ?? 0, controlError, stopConfirmation,
            recoveries && !active ? RecordingRecovery.Find(settings.ClipsRoot) : null);
        if (next == lastControlState) return;
        lastControlState = next;
        controlPublish(PipeMessages.RecordingControlState, next);
    }
}
