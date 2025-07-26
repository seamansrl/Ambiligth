
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using Timer = System.Timers.Timer;

namespace Ambiligth
{
    public partial class Main : Form
    {
        private int _storedVersion = 1;
        private SerialPort _serial;
        private CancellationTokenSource _cts;

        Bitmap GlobalFrame;

        // —— Campos para Ambilight ——
        private Boolean AmbilightRunning = false;
        private Timer _ambilightTimer;
        private const int AmbilightIntervalMs = 400; // El limite inferior puesto en la Arduino es de 300L asi que le damos 400ms como para no perder datos
        private readonly object _frameLock = new object();
        
        
        // INDICAR CUANTOS LEDS TIENE LA TIRA A USAR
        int LedsTotales = 24;


        // ---------- VENTANA PRINCIPAL ----------
        public Main()
        {
            InitializeComponent();
        }
        private async void Main_Load(object sender, EventArgs e)
        {
            // oculto hasta que todo esté listo
            this.Visible = false; 

            InitSerial();

            if (_serial != null && _serial.IsOpen)
            {
                await SendValueAsync("RST:");
                this.Visible = true;
            }
            else
            {
                Environment.Exit(1);
            }
        }
        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopAmbilight();

            if (_serial != null && _serial.IsOpen)
            {
                _serial.Close();
                _serial.Dispose();
                _serial = null;
            }

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        // ----------- SERIAL PORT COMUNICATION -----------
        private void InitSerial()
        {
            // 1) Encuentro el puerto NEOPIXEL
            string portName = FindNeoPixelBoxPort(_storedVersion);
            if (string.IsNullOrEmpty(portName))
            {
                MessageBox.Show(
                    $"No encontré ningún NEOPIXEL con versión mayor a {_storedVersion}.",
                    "Puerto no encontrado",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 2) Configuro el SerialPort SIN abrir aún
            _serial = new SerialPort(portName, 250000)
            {
                NewLine = "\n",
                Encoding = System.Text.Encoding.ASCII,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReceivedBytesThreshold = 1,
                ReadTimeout = 1000
            };

            // 3) Suscribo eventos ANTES de abrir
            _serial.DataReceived += Serial_DataReceived;
            _serial.ErrorReceived += (s, e) => Debug.WriteLine($"[Serial] Error: {e.EventType}");
            _serial.PinChanged += (s, e) => Debug.WriteLine($"[Serial] Pin: {e.EventType}");

            // 4) Abro y limpio buffers _viejos_
            _serial.Open();
            _serial.DiscardInBuffer();
            _serial.DiscardOutBuffer();

            Text += $"  [Puerto: {portName}]";
        }
        private string FindNeoPixelBoxPort(int currentVersion, int baudRate = 250000)
        {
            string bestPort = null;
            int bestVersion = currentVersion;

            foreach (var portName in SerialPort.GetPortNames())
            {
                using (var sp = new SerialPort(portName, baudRate))
                {
                    sp.NewLine = "\n";
                    sp.ReadTimeout = 10000;    // <-- espera hasta 10 segundos
                    try
                    {
                        sp.Open();
                        sp.DiscardInBuffer();
                        sp.WriteLine("RST:" + "\n");

                        // Bloquea hasta que llega línea o expira el timeout
                        string line = sp.ReadLine();

                        if (line.StartsWith("STA:STARTED|NEOPIXEL|", StringComparison.Ordinal))
                        {
                            var parts = line.Split('|');
                            if (parts.Length >= 3
                                && int.TryParse(parts[2], out int version)
                                && version >= bestVersion)
                            {
                                bestVersion = version;
                                bestPort = portName;
                            }
                        }
                    }
                    catch (TimeoutException)
                    {
                        // no llegó nada en 10 s: vamos al siguiente puerto
                    }
                    catch
                    {
                        // error abriendo/leyendo: lo ignoramos y seguimos
                    }
                    finally
                    {
                        if (sp.IsOpen)
                            sp.Close();

                        sp.Dispose();

                        Application.DoEvents();
                    }
                }
            }

            return bestPort;
        }

        private readonly StringBuilder _rxBuffer = new StringBuilder();
        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;      // te aseguras de leer del puerto correcto
                string chunk;

                try
                {
                    chunk = sp.ReadExisting();    // lee TODO lo que haya llegado
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error al leer puerto en DataReceived: {ex}");
                    return;
                }

                if (string.IsNullOrEmpty(chunk))
                    return;

                lock (_rxBuffer) // proteges el StringBuilder de accesos concurrentes
                {
                    _rxBuffer.Append(chunk);
                    string buf = _rxBuffer.ToString();
                    int lf;
                    // Mientras haya líneas completas...
                    while ((lf = buf.IndexOf('\n')) >= 0)
                    {
                        // Extraigo la línea sin CR/LF
                        string line = buf.Substring(0, lf).Trim('\r', '\n');

                        // Quito esa parte del buffer
                        _rxBuffer.Remove(0, lf + 1);

                        // Proceso la línea en el hilo de UI
                        BeginInvoke(new Action(() => ProcessLine(line)));

                        // Recalculo el string para ver si quedó otra línea
                        buf = _rxBuffer.ToString();
                    }
                }
            }
            catch { }
        }
        private void ProcessLine(string incomingData)
        {
            // Despierto al que esté esperando respuesta
            _respuestaPendiente?.TrySetResult(incomingData);
        }
        private async Task SendValueAsync(string value)
        {
            value = value.Replace(Environment.NewLine, "");

            if (_serial != null && _serial.IsOpen)
            {
                // Cortar en bloques de hasta 64 caracteres
                int chunkSize = 128;
                for (int i = 0; i < value.Length; i += chunkSize)
                {
                    // Verificamos si es el último bloque
                    bool esUltimo = (i + chunkSize >= value.Length);

                    // Sacamos el bloque correspondiente
                    string bloque = value.Substring(i, Math.Min(chunkSize, value.Length - i));

                    // Solo el último bloque lleva \n
                    if (esUltimo)
                        bloque += "\n";

                    // Preparo la espera de respuesta
                    _respuestaPendiente = new TaskCompletionSource<string>();

                    try
                    {
                        _serial.Write(bloque);
                    }
                    catch
                    {
                        return;
                    }

                    // Espero hasta 100ms o respuesta
                    var delay = Task.Delay(100);

                    if (_respuestaPendiente != null)
                    {
                        await Task.WhenAny(_respuestaPendiente.Task, delay);
                        // Limpio
                        _respuestaPendiente = null;
                    }
                }
            }
        }

        private TaskCompletionSource<string> _respuestaPendiente;

        // ------------ AMBILIGHT ------------ 
        public void StartAmbilight()
        {
            if (AmbilightRunning) return;
            AmbilightRunning = true;

            _ambilightTimer = new Timer(AmbilightIntervalMs);
            _ambilightTimer.AutoReset = true;
            _ambilightTimer.Elapsed += AmbilightTimer_Elapsed;
            _ambilightTimer.Start();
        }
        public async void StopAmbilight()
        {
            AmbilightRunning = false;
            if (_ambilightTimer == null) return;

            _ambilightTimer.Stop();
            _ambilightTimer.Elapsed -= AmbilightTimer_Elapsed;
            _ambilightTimer.Dispose();
            _ambilightTimer = null;

            Thread.Sleep(AmbilightIntervalMs); // Esperamos un poco para asegurarnos de que el timer se detuvo

            await SendValueAsync("NEO:64|000000|000000|000000|000000|000000|000000|000000|000000");
        }
        private async void AmbilightTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await AmbilightTick().ConfigureAwait(false);
        }
        private async Task AmbilightTick()
        {
            Rectangle virtualBounds = SystemInformation.VirtualScreen;

            var snapshot = new Bitmap(virtualBounds.Width, virtualBounds.Height);
            using (Graphics g = Graphics.FromImage(snapshot))
            {
                g.CopyFromScreen(virtualBounds.Location, System.Drawing.Point.Empty, virtualBounds.Size);
            }

            Mat mat = null;
            try
            {
                mat = BitmapConverter.ToMat(snapshot);

                Bitmap leftBmp = null, topBmp = null, rightBmp = null, miniBmp = null;
                try
                {
                    leftBmp = CapturarYReducirDesdeMat(mat, CalculateRoi(mat, "left"), 2, "left");
                    topBmp = CapturarYReducirDesdeMat(mat, CalculateRoi(mat, "top"), 4, "top");
                    rightBmp = CapturarYReducirDesdeMat(mat, CalculateRoi(mat, "right"), 2, "right");

                    miniBmp = CombinarHorizontalmente(new[] { leftBmp, topBmp, rightBmp });
                    if (miniBmp != null)
                        await ProcesarImagenAsync(miniBmp).ConfigureAwait(false);
                }
                finally
                {
                    if (leftBmp != null) leftBmp.Dispose();
                    if (topBmp != null) topBmp.Dispose();
                    if (rightBmp != null) rightBmp.Dispose();
                    if (miniBmp != null) miniBmp.Dispose();
                }
                
            }
            finally
            {
                if (mat != null) mat.Dispose();
                if (snapshot != null) snapshot.Dispose();
            }
        }
        private Bitmap CombinarHorizontalmente(Bitmap[] bitmaps)
        {
            if (bitmaps == null || bitmaps.Length == 0)
                return null;

            int totalWidth = 0;
            int maxHeight = 0;
            foreach (var bmp in bitmaps)
            {
                if (bmp == null) continue;
                totalWidth += bmp.Width;
                if (bmp.Height > maxHeight)
                    maxHeight = bmp.Height;
            }

            if (totalWidth == 0 || maxHeight == 0)
                return null;

            var result = new Bitmap(totalWidth, maxHeight);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Black);
                int offsetX = 0;
                foreach (var bmp in bitmaps)
                {
                    if (bmp == null) continue;
                    g.DrawImage(bmp, offsetX, 0, bmp.Width, bmp.Height);
                    offsetX += bmp.Width;
                }
            }
            return result;
        }
        private Bitmap CapturarYReducirDesdeMat(Mat mat, Rect roi, int franjas, string position)
        {
            // submat del área que toca
            Mat sub = null;
            Mat small = null;
            try
            {
                sub = new Mat(mat, roi);
                small = new Mat();

                if (position.Equals("left", StringComparison.InvariantCultureIgnoreCase) ||
                    position.Equals("right", StringComparison.InvariantCultureIgnoreCase))
                {
                    // 1) Reducimos a 1 px de ancho y 'franjas' de alto
                    Cv2.Resize(sub, small, new OpenCvSharp.Size(1, franjas), 0, 0, InterpolationFlags.Area);

                    // 2) Rotamos para obtener una línea horizontal
                    var rota = position.Equals("left", StringComparison.InvariantCultureIgnoreCase)
                        ? RotateFlags.Rotate90Clockwise
                        : RotateFlags.Rotate90Counterclockwise;

                    Mat tmp = null;
                    try
                    {
                        tmp = new Mat();
                        Cv2.Rotate(small, tmp, rota);
                        small.Dispose();
                        small = tmp;
                        tmp = null;
                    }
                    finally
                    {
                        if (tmp != null)
                            tmp.Dispose();
                    }
                }
                else // "top" u otras — línea horizontal directa
                {
                    Cv2.Resize(sub, small, new OpenCvSharp.Size(franjas, 1), 0, 0, InterpolationFlags.Area);
                }

                return small.ToBitmap();
            }
            finally
            {
                if (small != null)
                    small.Dispose();
                if (sub != null)
                    sub.Dispose();
            }
        }
        private OpenCvSharp.Rect CalculateRoi(Mat mat, string position)
        {
            if (string.IsNullOrEmpty(position))
                position = "top";  // por defecto

            int totalW = mat.Width;
            int totalH = mat.Height;

            switch (position.ToLowerInvariant())
            {
                case "left":
                    {
                        int regionW = totalW / 4;
                        return new OpenCvSharp.Rect(0, 0, regionW, totalH);
                    }

                case "right":
                    {
                        int regionW = totalW / 4;
                        return new OpenCvSharp.Rect(totalW - regionW, 0, regionW, totalH);
                    }

                case "top":
                default:
                    {
                        int regionH = totalH / 4;
                        return new OpenCvSharp.Rect(0, 0, totalW, regionH);
                    }
            }
        }
        private async Task ProcesarImagenAsync(Bitmap imagen)
        {

            if (imagen.Width > 128)
            {
                MessageBox.Show("La imagen debe tener 128 píxeles como máximo");
                return;
            }

            List<string> salidas = new List<string>();

            for (int y = 0; y < imagen.Height; y++)
            {
                List<string> lineaHex = new List<string>();
                for (int x = 0; x < imagen.Width; x++)
                {
                    Color pixel = imagen.GetPixel(x, y);
                    string hex = $"{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
                    lineaHex.Add(hex);
                }

                string lineaSalida = "NEO:" + LedsTotales.ToString() + "|" + string.Join("|", lineaHex) + Environment.NewLine;
                await SendValueAsync(lineaSalida);
            }
        }

        private void Iniciar_Click(object sender, EventArgs e)
        {
            StartAmbilight();
        }

        private void Parar_Click(object sender, EventArgs e)
        {
            StopAmbilight();
        }
    }
}
