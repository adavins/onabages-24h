namespace OnaBages24.Engine.Playback;

/// <summary>
/// Fil de control del motor (docs/disseny-motor.md, secció 3): fa córrer l'AutoDJ i la cua cada 10 ms i comprova la
/// sortida cada mig segon. No és el fil d'àudio: pot esperar uns mil·lisegons sense que se senti, perquè tot es programa
/// en fotogrames amb antelació. Un error aquí no atura res: queda al registre (com a molt un cop cada 10 s) i continua.
/// </summary>
public sealed class ControlLoop : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan OutputInterval = TimeSpan.FromMilliseconds(500);

    private readonly QueuePlayer _queue;
    private readonly OutputManager _outputs;
    private readonly AutoDj? _autoDj;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public ControlLoop(QueuePlayer queue, OutputManager outputs, ILogger log, AutoDj? autoDj = null)
    {
        _queue = queue;
        _outputs = outputs;
        _autoDj = autoDj;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Control del motor", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        var token = _stop.Token;
        var lastOutputCheck = DateTime.MinValue;
        var lastError = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastOutputCheck >= OutputInterval)
                {
                    lastOutputCheck = DateTime.UtcNow;
                    _outputs.Ensure();
                }
                // Primer l'AutoDJ: si la cua era buida, la pista que hi posa comença a preparar-se en aquesta mateixa volta
                try { _autoDj?.Tick(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Un error de l'AutoDJ (p. ex. la base de dades) no pot impedir que la cua continuï sonant
                    if (DateTime.UtcNow - lastError > TimeSpan.FromSeconds(10))
                    {
                        lastError = DateTime.UtcNow;
                        _log.LogError("Error a l'AutoDJ (continua): {Error}", ex);
                    }
                }
                _queue.Tick();
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - lastError > TimeSpan.FromSeconds(10))
                {
                    lastError = DateTime.UtcNow;
                    _log.LogError("Error al fil de control (continua): {Error}", ex);
                }
            }
            token.WaitHandle.WaitOne(TickInterval);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
