using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Owin.Hosting;
using Owin;
using System.Web.Http;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net;
using System.Collections.Concurrent;
using NLog;

namespace HeartBeatServer
{
    public enum FuzzyStatus
    {
        FullyOnline,
        MostlyOnline,
        PossiblyUnstable,
        LikelyOffline,
        Offline
    }

    public partial class Form1 : Form
    {
        private IDisposable webApp;
        private ConcurrentDictionary<string, DateTime> nodeHeartbeats = new ConcurrentDictionary<string, DateTime>();
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private List<System.Diagnostics.Process> workerProcesses = new List<System.Diagnostics.Process>();

        // UI controls
        private NumericUpDown numericUpDownWorkers;
        private Button buttonApplyWorkers;
        private ListBox listBoxWorkers;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel toolStripStatusLabel;
        private GroupBox groupBoxWorkers;

        public Form1()
        {
            InitializeComponent();

            // --- DataGridView setup ---
            dataGridView1.Dock = DockStyle.Top;
            dataGridView1.Height = 250;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            if (dataGridView1.Columns.Count == 0)
            {
                dataGridView1.Columns.Add("NodeId", "NodeId");
                dataGridView1.Columns.Add("LastHeartbeat", "LastHeartbeat");
                dataGridView1.Columns.Add("FuzzyStatus", "FuzzyStatus");
            }
            dataGridView1.CellFormatting += DataGridView1_CellFormatting;

            // --- GroupBox for worker management ---
            groupBoxWorkers = new GroupBox
            {
                Text = "Worker Management",
                Location = new Point(10, dataGridView1.Bottom + 10),
                Size = new Size(400, 120)
            };
            this.Controls.Add(groupBoxWorkers);

            // Label for NumericUpDown
            var label = new Label
            {
                Text = "Desired Worker Count:",
                Location = new Point(10, 25),
                AutoSize = true
            };
            groupBoxWorkers.Controls.Add(label);

            // NumericUpDown for worker count
            numericUpDownWorkers = new NumericUpDown
            {
                Name = "numericUpDownWorkers",
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Location = new Point(label.Right + 10, 20),
                Width = 60
            };
            groupBoxWorkers.Controls.Add(numericUpDownWorkers);

            // Button to apply worker count
            buttonApplyWorkers = new Button
            {
                Name = "buttonApplyWorkers",
                Text = "Apply Workers",
                Location = new Point(numericUpDownWorkers.Right + 10, 18),
                Width = 100
            };
            groupBoxWorkers.Controls.Add(buttonApplyWorkers);

            // ListBox to show worker PIDs
            listBoxWorkers = new ListBox
            {
                Name = "listBoxWorkers",
                Location = new Point(10, buttonApplyWorkers.Bottom + 10),
                Width = 370,
                Height = 50
            };
            groupBoxWorkers.Controls.Add(listBoxWorkers);

            // StatusStrip at the bottom
            statusStrip = new StatusStrip();
            toolStripStatusLabel = new ToolStripStatusLabel("Ready");
            statusStrip.Items.Add(toolStripStatusLabel);
            statusStrip.Dock = DockStyle.Bottom;
            this.Controls.Add(statusStrip);

            // Wire up event
            buttonApplyWorkers.Click += buttonApplyWorkers_Click;
            timer1.Tick += timer1_Tick;
            this.Load += new System.EventHandler(this.Form1_Load);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            StartWebApi();
            timer1.Interval = 500;
            timer1.Start();
        }

        private void StartWebApi()
        {
            string baseAddress = "http://localhost:9000/";
            webApp = WebApp.Start(baseAddress, appBuilder =>
            {
                HttpConfiguration config = new HttpConfiguration();
                config.Routes.MapHttpRoute(
                    name: "DefaultApi",
                    routeTemplate: "api/{controller}/{id}",
                    defaults: new { id = RouteParameter.Optional }
                );
                appBuilder.UseWebApi(config);
            });
        }

        // Only use server-side timestamp
        public void UpdateHeartbeat(string nodeId)
        {
            var now = DateTime.UtcNow;
            nodeHeartbeats[nodeId] = now;
            logger.Info($"Heartbeat received from {nodeId} at {now:O}");
        }

        private FuzzyStatus GetFuzzyStatus(DateTime lastHeartbeat)
        {
            var diff = (DateTime.UtcNow - lastHeartbeat).TotalSeconds;
            if (diff <= 2) return FuzzyStatus.FullyOnline;
            if (diff <= 5) return FuzzyStatus.MostlyOnline;
            if (diff <= 8) return FuzzyStatus.PossiblyUnstable;
            if (diff <= 12) return FuzzyStatus.LikelyOffline;
            return FuzzyStatus.Offline;
        }

        private string GetFuzzyStatusString(FuzzyStatus status)
        {
            switch (status)
            {
                case FuzzyStatus.FullyOnline: return "Fully Online";
                case FuzzyStatus.MostlyOnline: return "Mostly Online";
                case FuzzyStatus.PossiblyUnstable: return "Possibly Unstable";
                case FuzzyStatus.LikelyOffline: return "Likely Offline";
                case FuzzyStatus.Offline: return "Offline";
                default: return "Unknown";
            }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            foreach (var kvp in nodeHeartbeats)
            {
                FuzzyStatus fuzzy = GetFuzzyStatus(kvp.Value);
                UpdateNodeStatusUI(kvp.Key, kvp.Value, fuzzy);
            }

            // Update status bar
            int online = nodeHeartbeats.Count(kvp => GetFuzzyStatus(kvp.Value) == FuzzyStatus.FullyOnline || GetFuzzyStatus(kvp.Value) == FuzzyStatus.MostlyOnline);
            int total = nodeHeartbeats.Count;
            toolStripStatusLabel.Text = $"Online: {online}/{total} | Last update: {DateTime.Now:HH:mm:ss}";
        }

        private void UpdateNodeStatusUI(string nodeId, DateTime lastHeartbeat, FuzzyStatus fuzzy)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateNodeStatusUI(nodeId, lastHeartbeat, fuzzy)));
                return;
            }

            string lastHeartbeatLocal = lastHeartbeat.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            var row = dataGridView1.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(r => r.Cells[0].Value?.ToString() == nodeId);

            if (row == null)
                dataGridView1.Rows.Add(nodeId, lastHeartbeatLocal, GetFuzzyStatusString(fuzzy));
            else
            {
                row.Cells[1].Value = lastHeartbeatLocal;
                row.Cells[2].Value = GetFuzzyStatusString(fuzzy);
            }
        }

        // Color rows based on fuzzy status
        private void DataGridView1_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (dataGridView1.Columns[e.ColumnIndex].Name == "FuzzyStatus" && e.Value != null)
            {
                string status = e.Value.ToString();
                DataGridViewRow row = dataGridView1.Rows[e.RowIndex];
                switch (status)
                {
                    case "Fully Online":
                        row.DefaultCellStyle.BackColor = Color.LightGreen;
                        break;
                    case "Mostly Online":
                        row.DefaultCellStyle.BackColor = Color.LightYellow;
                        break;
                    case "Possibly Unstable":
                        row.DefaultCellStyle.BackColor = Color.Orange;
                        break;
                    case "Likely Offline":
                        row.DefaultCellStyle.BackColor = Color.OrangeRed;
                        break;
                    case "Offline":
                        row.DefaultCellStyle.BackColor = Color.LightGray;
                        break;
                    default:
                        row.DefaultCellStyle.BackColor = Color.Black;
                        break;
                }
            }
        }

        private void ApplyWorkerCount(int desiredCount)
        {
            int currentCount = workerProcesses.Count;

            if (desiredCount > currentCount)
            {
                for (int i = 0; i < desiredCount - currentCount; i++)
                {
                    var proc = System.Diagnostics.Process.Start("D:/PlayGround/python/HeartBeatClient/dist/worker_node.exe"); // Replace with your worker exe name
                    workerProcesses.Add(proc);
                    listBoxWorkers.Items.Add($"PID: {proc.Id}");
                }
            }
            else if (desiredCount < currentCount)
            {
                for (int i = 0; i < currentCount - desiredCount; i++)
                {
                    var proc = workerProcesses[0];
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill();
                    }
                    catch { /* handle errors if needed */ }
                    workerProcesses.RemoveAt(0);
                    if (listBoxWorkers.Items.Count > 0)
                        listBoxWorkers.Items.RemoveAt(0);
                }
            }
        }

        private void buttonApplyWorkers_Click(object sender, EventArgs e)
        {
            int desiredCount = (int)numericUpDownWorkers.Value;
            ApplyWorkerCount(desiredCount);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            foreach (var proc in workerProcesses)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill();
                }
                catch { }
            }

            if (webApp != null)
            {
                webApp.Dispose();
                webApp = null;
            }

            base.OnFormClosing(e);
        }


        private void Form1_Load_1(object sender, EventArgs e)
        {

        }
    }

    public class HeartbeatController : ApiController
    {
        [HttpPost]
        public IHttpActionResult Post([FromBody] HeartbeatMessage msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.NodeId)) return BadRequest();

            var mainForm = System.Windows.Forms.Application.OpenForms.Count > 0
                ? (Form1)System.Windows.Forms.Application.OpenForms[0]
                : null;

            if (mainForm != null)
            {
                mainForm.UpdateHeartbeat(msg.NodeId);
                return Ok();
            }
            else
            {
                return InternalServerError(new Exception("Server is shutting down. No forms available."));
            }
        }

    }

    public class HeartbeatMessage
    {
        public string NodeId { get; set; }
        // Timestamp property is ignored by the server
    }
}
