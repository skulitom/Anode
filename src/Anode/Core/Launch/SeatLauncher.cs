using System.Runtime.InteropServices;
using Anode.Core.Util;

namespace Anode.Core.Launch;

/// <summary>
/// Starts the first process inside the child session.
///
/// A process in the parent session cannot simply <c>Process.Start</c> something into
/// another session: the child would inherit the parent's session id. The Task
/// Scheduler can, through <c>IRegisteredTask::RunEx</c> with TASK_RUN_USE_SESSION_ID,
/// and it does so as the same interactive user with no elevation and no extra rights.
/// Anode uses it once, to place the seat host inside the seat. After that the seat
/// host starts everything else itself, because it is already in the right session.
/// </summary>
internal static class SeatLauncher
{
    private const int TaskActionExec = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskRunLevelHighest = 1;
    private const int TaskRunUseSessionId = 0x4;

    public static void LaunchInSession(
        uint sessionId,
        string executable,
        string arguments,
        string workingDirectory,
        bool elevated = false)
    {
        if (sessionId == 0 || sessionId == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sessionId), $"Session {sessionId} is not a usable target.");

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("The Task Scheduler COM service is not available on this machine.");

        string taskName = $"Anode-SeatLaunch-{Guid.NewGuid():N}";
        string account = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

        object? scheduler = null, folder = null, definition = null, action = null, registered = null, running = null;
        bool taskRegistered = false;

        try
        {
            scheduler = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("Could not create the Task Scheduler COM object.");
            dynamic service = scheduler;
            service.Connect();

            folder = service.GetFolder("\\");
            dynamic root = folder;

            definition = service.NewTask(0);
            dynamic task = definition;
            task.RegistrationInfo.Author = "Anode";
            task.RegistrationInfo.Description =
                $"Temporary: start {Path.GetFileName(executable)} inside Anode seat (session {sessionId}).";

            task.Settings.Enabled = true;
            task.Settings.Hidden = true;
            task.Settings.AllowDemandStart = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.RunOnlyIfIdle = false;
            task.Settings.MultipleInstances = 3; // parallel
            task.Settings.ExecutionTimeLimit = "PT0S"; // never killed by the scheduler

            task.Principal.UserId = account;
            task.Principal.LogonType = TaskLogonInteractiveToken;
            task.Principal.RunLevel = elevated ? TaskRunLevelHighest : TaskRunLevelLua;

            action = task.Actions.Create(TaskActionExec);
            dynamic exec = action;
            exec.Path = executable;
            exec.Arguments = arguments;
            exec.WorkingDirectory = workingDirectory;

            registered = root.RegisterTaskDefinition(
                taskName, definition, TaskCreate, account, null, TaskLogonInteractiveToken, null);
            taskRegistered = true;

            dynamic registeredTask = registered;
            running = registeredTask.RunEx(null, TaskRunUseSessionId, checked((int)sessionId), null);

            if (running is null)
                throw new InvalidOperationException("The Task Scheduler accepted the request but returned no running instance.");

            Log.Info($"launched into session {sessionId}: {executable} {arguments}");
        }
        finally
        {
            if (taskRegistered && folder is not null)
            {
                try { ((dynamic)folder).DeleteTask(taskName, 0); }
                catch (COMException) { /* the program already started; a stale hidden task is not worth failing over */ }
            }

            Release(running); Release(registered); Release(action);
            Release(definition); Release(folder); Release(scheduler);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }
    }
}
