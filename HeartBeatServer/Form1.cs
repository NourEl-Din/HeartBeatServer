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
using System.Windows.Forms.DataVisualization.Charting;
using System.Diagnostics;

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
        private ConcurrentDictionary<string, double> nodeIntervals = new ConcurrentDictionary<string, double>();
        private List<Process> workerProcesses = new List<Process>();
        private Dictionary<string, DateTime> autoRecoveryTimestamps = new Dictionary<string, DateTime>();
        private HashSet<string> ignoredOfflineNodes = new HashSet<string>();

        //private DataGridView dataGridView1;
        private Chart chartNodeStatus;
        private GroupBox groupBoxWorkers;
        private NumericUpDown numericUpDownWorkers;
        private Button buttonApplyWorkers;
        private ListBox listBoxWorkers;
        private Button buttonSimulateFault;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel toolStripStatusLabel;
        private Dictionary<int, string> pidToNodeId = new Dictionary<int, string>();
        //private Dictionary<int, string> pidToNodeId = new Dictionary<int, string>();
        //private Timer timer1 = new Timer();

        public Form1()
        {
            InitializeComponent();

            // === TableLayoutPanel ===
            var tableLayout = new TableLayoutPanel();
            tableLayout.Dock = DockStyle.Fill;
            tableLayout.RowCount = 3;
            tableLayout.ColumnCount = 1;
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 250)); // DataGridView height
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); // GroupBox height
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Chart fills remaining
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // === DataGridView ===
            dataGridView1.Dock = DockStyle.Fill;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dataGridView1.Columns.Add("NodeId", "NodeId");
            dataGridView1.Columns.Add("LastHeartbeat", "LastHeartbeat");
            dataGridView1.Columns.Add("FuzzyStatus", "FuzzyStatus");
            dataGridView1.Columns.Add("RecommendedInterval", "Recommended Interval (s)");
            dataGridView1.CellFormatting += DataGridView1_CellFormatting;

            // === GroupBox ===
            groupBoxWorkers = new GroupBox();
            groupBoxWorkers.Text = "Worker Controls";
            groupBoxWorkers.Dock = DockStyle.Fill;
            groupBoxWorkers.MinimumSize = new Size(0, 50);

            Label label = new Label { Text = "Desired Workers:", Location = new System.Drawing.Point(10, 25) };
            groupBoxWorkers.Controls.Add(label);

            numericUpDownWorkers = new NumericUpDown { Minimum = 0, Maximum = 100, Location = new System.Drawing.Point(120, 22), Width = 50 };
            groupBoxWorkers.Controls.Add(numericUpDownWorkers);

            buttonApplyWorkers = new Button { Text = "Apply Workers", Location = new System.Drawing.Point(180, 20), Width = 100 };
            buttonApplyWorkers.Click += ButtonApplyWorkers_Click;
            groupBoxWorkers.Controls.Add(buttonApplyWorkers);

            listBoxWorkers = new ListBox { Location = new System.Drawing.Point(10, 60), Width = 270, Height = 40 };
            groupBoxWorkers.Controls.Add(listBoxWorkers);

            buttonSimulateFault = new Button { Text = "Simulate Fault for Selected", Location = new System.Drawing.Point(10, 105), Width = 270 };
            buttonSimulateFault.Click += ButtonSimulateFault_Click;
            groupBoxWorkers.Controls.Add(buttonSimulateFault);

            // === Chart ===
            chartNodeStatus = new Chart();
            chartNodeStatus.Dock = DockStyle.Fill;
            ChartArea chartArea = new ChartArea("NodeStatus");
            chartNodeStatus.ChartAreas.Add(chartArea);
            chartNodeStatus.ChartAreas[0].AxisX.LabelStyle.Format = "HH:mm:ss";
            chartNodeStatus.ChartAreas[0].AxisX.IntervalType = DateTimeIntervalType.Seconds;
            chartNodeStatus.ChartAreas[0].AxisX.MajorGrid.LineColor = Color.LightGray;
            chartNodeStatus.ChartAreas[0].AxisY.Minimum = 0.0;
            chartNodeStatus.ChartAreas[0].AxisY.Maximum = 1.0;

            // === StatusStrip ===
            statusStrip = new StatusStrip();
            toolStripStatusLabel = new ToolStripStatusLabel("Ready");
            statusStrip.Items.Add(toolStripStatusLabel);
            statusStrip.Dock = DockStyle.Bottom;

            // === Add controls to TableLayoutPanel ===
            tableLayout.Controls.Add(dataGridView1, 0, 0);
            tableLayout.Controls.Add(groupBoxWorkers, 0, 1);
            tableLayout.Controls.Add(chartNodeStatus, 0, 2);

            // === Add TableLayoutPanel and StatusStrip to Form ===
            this.Controls.Add(statusStrip);
            this.Controls.Add(tableLayout);

            // === Timer ===
            timer1.Interval = 500;
            timer1.Tick += Timer1_Tick;
            timer1.Start();

            // === Start Web API ===
            StartWebApi();
        }

        private void StartWebApi()
        {
            string baseAddress = "http://localhost:9000/";
            webApp = WebApp.Start(baseAddress, appBuilder =>
            {
                HttpConfiguration config = new HttpConfiguration();
                config.Routes.MapHttpRoute("API Default", "api/{controller}/{id}", new { id = RouteParameter.Optional });
                appBuilder.UseWebApi(config);
            });
        }

        public (FuzzyStatus, double) UpdateHeartbeatAndGetStatus(string nodeId)
        {
            var now = DateTime.UtcNow;
            nodeHeartbeats[nodeId] = now;

            // Reactivate node if previously ignored
            ignoredOfflineNodes.Remove(nodeId);

            if (int.TryParse(nodeId, out int pid))
            {
                if (workerProcesses.Any(p => p.Id == pid))
                {
                    pidToNodeId[pid] = nodeId;
                }
            }

            var fuzzy = GetFuzzyStatus(now);
            var interval = GetRecommendedInterval(fuzzy);
            nodeIntervals[nodeId] = interval;

            return (fuzzy, interval);
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

        private double GetRecommendedInterval(FuzzyStatus status)
        {
            switch (status)
            {
                case FuzzyStatus.FullyOnline: return 1.0;
                case FuzzyStatus.MostlyOnline: return 0.5;
                case FuzzyStatus.PossiblyUnstable: return 0.3;
                case FuzzyStatus.LikelyOffline: return 0.1;
                default: return 0.1;
            }
        }

        private void Timer1_Tick(object sender, EventArgs e)
        {
            foreach (var kvp in nodeHeartbeats)
            {
                FuzzyStatus fuzzy = GetFuzzyStatus(kvp.Value);
                double interval = GetRecommendedInterval(fuzzy);
                nodeIntervals[kvp.Key] = interval;
                UpdateNodeStatusUI(kvp.Key, kvp.Value, fuzzy, interval);
            }

            // Auto-recovery: restart offline workers with cooldown (10 seconds)
            foreach (var kvp in nodeHeartbeats)
            {
                string nodeId = kvp.Key;
                FuzzyStatus status = GetFuzzyStatus(kvp.Value);

                if (ignoredOfflineNodes.Contains(nodeId))
                    continue; // Skip already handled nodes

                if ((status == FuzzyStatus.Offline || status == FuzzyStatus.LikelyOffline) && int.TryParse(nodeId, out int pid))
                {
                    var proc = workerProcesses.FirstOrDefault(p => p.Id == pid);
                    bool needRecovery = (proc == null || proc.HasExited);

                    if (needRecovery && (!autoRecoveryTimestamps.ContainsKey(nodeId) ||
                        (DateTime.Now - autoRecoveryTimestamps[nodeId]).TotalSeconds > 10))
                    {
                        try
                        {
                            var newProc = Process.Start("D:/PlayGround/python/HeartBeatClient/dist/worker_node.exe");
                            workerProcesses.Add(newProc);
                            listBoxWorkers.Items.Add($"PID: {newProc.Id}");
                            pidToNodeId[newProc.Id] = newProc.Id.ToString();
                            autoRecoveryTimestamps[nodeId] = DateTime.Now;
                            toolStripStatusLabel.Text = $"Auto-recovered worker PID: {newProc.Id}";

                            // Mark node as handled
                            ignoredOfflineNodes.Add(nodeId);
                        }
                        catch (Exception ex)
                        {
                            toolStripStatusLabel.Text = $"Auto-recovery failed: {ex.Message}";
                        }
                    }
                }
            }


            int online = nodeHeartbeats.Count(kvp => GetFuzzyStatus(kvp.Value) != FuzzyStatus.Offline);
            toolStripStatusLabel.Text = $"Online: {online}/{nodeHeartbeats.Count} | {DateTime.Now:T}";
        }

        private void UpdateNodeStatusUI(string nodeId, DateTime lastHeartbeat, FuzzyStatus fuzzy, double interval)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateNodeStatusUI(nodeId, lastHeartbeat, fuzzy, interval)));
                return;
            }

            string lastHeartbeatLocal = lastHeartbeat.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var row = dataGridView1.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Cells[0].Value?.ToString() == nodeId);

            if (row == null)
                dataGridView1.Rows.Add(nodeId, lastHeartbeatLocal, fuzzy.ToString(), interval.ToString("0.0"));
            else
            {
                // Always update all columns, even if placeholder
                row.Cells[1].Value = lastHeartbeatLocal;
                row.Cells[2].Value = fuzzy.ToString();
                row.Cells[3].Value = interval.ToString("0.0");
            }

            if (chartNodeStatus.Series.IsUniqueName(nodeId))
                chartNodeStatus.Series.Add(new Series(nodeId) { ChartType = SeriesChartType.Line });

            double score = fuzzy == FuzzyStatus.FullyOnline ? 1.0 :
                           fuzzy == FuzzyStatus.MostlyOnline ? 0.7 :
                           fuzzy == FuzzyStatus.PossiblyUnstable ? 0.4 :
                           fuzzy == FuzzyStatus.LikelyOffline ? 0.1 : 0.0;

            var series = chartNodeStatus.Series[nodeId];
            series.Points.AddXY(DateTime.Now, score);

            // Limit to last 100 points for readability
            if (series.Points.Count > 100)
                series.Points.RemoveAt(0);

            chartNodeStatus.Invalidate(); // Force redraw
        }

        private void DataGridView1_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (dataGridView1.Columns[e.ColumnIndex].Name == "FuzzyStatus" && e.Value != null)
            {
                string status = e.Value.ToString();
                var row = dataGridView1.Rows[e.RowIndex];
                switch (status)
                {
                    case "FullyOnline": row.DefaultCellStyle.BackColor = System.Drawing.Color.LightGreen; break;
                    case "MostlyOnline": row.DefaultCellStyle.BackColor = System.Drawing.Color.LightYellow; break;
                    case "PossiblyUnstable": row.DefaultCellStyle.BackColor = System.Drawing.Color.Orange; break;
                    case "LikelyOffline": row.DefaultCellStyle.BackColor = System.Drawing.Color.OrangeRed; break;
                    case "Offline": row.DefaultCellStyle.BackColor = System.Drawing.Color.LightGray; break;
                    default: row.DefaultCellStyle.BackColor = System.Drawing.Color.White; break;
                }
            }
        }

        private void ButtonApplyWorkers_Click(object sender, EventArgs e)
        {
            int desiredCount = (int)numericUpDownWorkers.Value;
            while (workerProcesses.Count < desiredCount)
            {
                var proc = System.Diagnostics.Process.Start("D:/PlayGround/python/HeartBeatClient/dist/worker_node.exe");
                workerProcesses.Add(proc);
                listBoxWorkers.Items.Add($"PID: {proc.Id}");

                // Add placeholder to grid
                string nodeId = proc.Id.ToString();
                var row = dataGridView1.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Cells[0].Value?.ToString() == nodeId);
                if (row == null)
                    dataGridView1.Rows.Add(nodeId, "Waiting for heartbeat...", "Starting...", "");
            }
            while (workerProcesses.Count > desiredCount)
            {
                var proc = workerProcesses[0];
                if (!proc.HasExited) proc.Kill();
                workerProcesses.RemoveAt(0);
                if (listBoxWorkers.Items.Count > 0) listBoxWorkers.Items.RemoveAt(0);
            }
        }

        private void ButtonSimulateFault_Click(object sender, EventArgs e)
        {
            if (listBoxWorkers.SelectedIndex >= 0)
            {
                var pidStr = listBoxWorkers.SelectedItem.ToString().Replace("PID: ", "");
                int pid = int.Parse(pidStr);

                // Use the mapped nodeId if available
                if (!pidToNodeId.TryGetValue(pid, out string nodeId))
                {
                    MessageBox.Show("NodeId for this PID is not known yet. Wait for the worker to send a heartbeat.");
                    return;
                }

                // Only simulate if the node is known
                if (!nodeHeartbeats.ContainsKey(nodeId))
                {
                    MessageBox.Show("This node has not sent a heartbeat yet.");
                    return;
                }

                // Set the last heartbeat to 6 seconds ago to make it "PossiblyUnstable" right now
                nodeHeartbeats[nodeId] = DateTime.UtcNow - TimeSpan.FromSeconds(6);

                // Immediately update the UI to reflect the change
                var fuzzy = GetFuzzyStatus(nodeHeartbeats[nodeId]);
                var interval = GetRecommendedInterval(fuzzy);
                nodeIntervals[nodeId] = interval;
                UpdateNodeStatusUI(nodeId, nodeHeartbeats[nodeId], fuzzy, interval);

                MessageBox.Show($"Simulated 'unstable' for PID: {pid} (nodeId: {nodeId})");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            foreach (var proc in workerProcesses)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.CloseMainWindow();
                        proc.WaitForExit(1000); // Wait 1 second for graceful exit
                        if (!proc.HasExited)
                            proc.Kill();
                    }
                }
                catch { }
            }
            workerProcesses.Clear();
            if (webApp != null) webApp.Dispose();
            base.OnFormClosing(e);
        }
    }

    public class HeartbeatController : ApiController
    {
        [HttpPost]
        public IHttpActionResult Post([FromBody] HeartbeatMessage msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.NodeId)) return BadRequest();
            var mainForm = System.Windows.Forms.Application.OpenForms[0] as Form1;
            var (fuzzy, interval) = mainForm.UpdateHeartbeatAndGetStatus(msg.NodeId);
            return Ok(new { msg.NodeId, fuzzyStatus = fuzzy.ToString(), heartbeatInterval = interval });
        }
    }

    public class HeartbeatMessage { public string NodeId { get; set; } }
}
