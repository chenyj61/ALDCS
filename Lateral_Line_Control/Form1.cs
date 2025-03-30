using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace Lateral_Line_Control
{
    public partial class f_main : Form
    {
        //实例化串口对象
        SerialPort serialPort = new SerialPort();
        List<byte> buffer = new List<byte>();
        int frame_size = 2 + 8 + 4 * 24 + 2; // 帧头2字节 + 计数器8字节 + 24通道数据(4字节每通道) + 帧尾2字节
        int num_channels = 24;
        List<double[]> channels_data = new List<double[]>(); // 存储通道数据
        List<double[]> channels_data_cali = new List<double[]>(); // 存储用于校准通道数据
        Boolean sampleFlag = false;
        Boolean caliFlag = false;
        int Cnt_Cali = 0;
        double[] sensorAverages = new double[24];
        Int64 NumOfSample = 0;
        Int64 TimeOfSample = 0;

        public f_main()
        {
            InitializeComponent();
            btn_Cali.ForeColor = Color.Green;
            InitializeChart();
        }
        // 更新Chart控件
        private void UpdateChart(double[] channel_values)
        {
            // 因为串口接收是在后台线程中进行的，因此需要使用Invoke来更新UI
            if (crt_DataLoger.InvokeRequired)
            {
                crt_DataLoger.Invoke(new Action(() => UpdateChart(channel_values)));
            }
            else
            {
                // 更新每个系列的数据
                for (int i = 0; i < num_channels; i++)
                {
                    Series series = crt_DataLoger.Series[i];

                    // 添加新的点
                    series.Points.AddY(channel_values[i]);

                    // 控制X轴上显示点的数量（例如，最多显示100个点）
                    if (series.Points.Count > 100)
                    {
                        series.Points.RemoveAt(0); // 移除最早的点
                    }

                }
                crt_DataLoger.ChartAreas[0].RecalculateAxesScale(); // 重新计算轴的缩放
                // 更新X轴范围（可选）
                //crt_DataLoger.ChartAreas[0].AxisX.Minimum = Math.Max(0, crt_DataLoger.Series[0].Points.Count - 100);
                //crt_DataLoger.ChartAreas[0].AxisX.Maximum = crt_DataLoger.Series[0].Points.Count;
            }
        }

        // 初始化Chart控件
        private void InitializeChart()
        {
            // 清除已有的series
            crt_DataLoger.Series.Clear();

            // 创建24个系列，每个系列对应一个通道
            for (int i = 0; i < num_channels; i++)
            {
                Series series = new Series($"Channel {i + 1}");
                series.ChartType = SeriesChartType.Line; // 设置为折线图
                series.BorderWidth = 2; // 设置线条宽度
                crt_DataLoger.Series.Add(series);
            }

            // 设置Chart控件的其他属性
            crt_DataLoger.ChartAreas[0].AxisX.Title = "Data Points";
            crt_DataLoger.ChartAreas[0].AxisY.Title = "Pressure (Pa)";
            crt_DataLoger.ChartAreas[0].AxisX.Minimum = double.NaN; ;
            crt_DataLoger.ChartAreas[0].AxisX.Maximum = double.NaN; ; // 可以根据需要设置X轴的显示范围
            crt_DataLoger.ChartAreas[0].AxisY.Minimum = double.NaN; ; // 设置Y轴最小值，可以根据数据范围调整
            crt_DataLoger.ChartAreas[0].AxisY.Maximum = double.NaN; ; // 设置Y轴最大值
        }

        private void btn_Path_Select_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog fileDialog = new FolderBrowserDialog();
            if (fileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                label2.Text = fileDialog.SelectedPath;
            }
        }

        private void f_main_Load(object sender, EventArgs e)
        {
            /*------串口设置------*/
            //检查是否含有串口
            string[] str = SerialPort.GetPortNames();
            if (str == null)
            {
                MessageBox.Show("本机没有串口！", "Error");
                return;
            }
            //添加串口
            foreach (string s in str)
            {
                comboBoxCom.Items.Add(s);
            }
            /*------波特率设置-------*/
            string[] baudRate = { "9600", "19200", "38400", "57600", "115200" };
            foreach (string s in baudRate)
            {
                comboBoxBaudRate.Items.Add(s);
            }
            comboBoxBaudRate.SelectedIndex = 0;
            Control.CheckForIllegalCrossThreadCalls = false;
            this.DoubleBuffered = true;  // 启用双缓冲
            //准备就绪              
            serialPort.DtrEnable = true;
            serialPort.RtsEnable = true;
            //设置数据读取超时为1秒
            serialPort.ReadTimeout = 1000;

            serialPort.Close();
        }
        private bool isReceiving = true;  // 标志位，默认允许接收数据
        //接收数据
        private void dataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (!isReceiving) return;  // 如果标志位为false，立即退出

            if (serialPort.IsOpen)
            {
                //输出当前时间
                DateTime dateTimeNow = DateTime.Now;
                //dateTimeNow.GetDateTimeFormats();
                textBoxReceive.Text += string.Format("{0}<-", dateTimeNow);
                //dateTimeNow.GetDateTimeFormats('f')[0].ToString() + "\r\n";
                textBoxReceive.ForeColor = Color.Red;    //改变字体的颜色
                textBoxReceive.SelectionStart = textBoxReceive.Text.Length;
                textBoxReceive.ScrollToCaret();//滚动到光标处
                try
                {
                    if (serialPort.BytesToRead > 0)
                    {
                        byte[] new_data = new byte[500];
                        int bytesRead = serialPort.Read(new_data, 0, new_data.Length); // 读取串口数据
                        buffer.AddRange(new_data.Take(bytesRead)); // 将读取到的新数据添加到缓冲区
                    }

                    // 查找帧头和帧尾
                    while (buffer.Count >= frame_size)
                    {
                        // 查找帧头 0xAA 0xBB
                        int start_idx = buffer.FindIndex(b => b == 0xAA);
                        if (start_idx == -1 || start_idx + 1 >= buffer.Count || buffer[start_idx + 1] != 0xBB)
                        {
                            if (buffer.Count > 1)
                            {
                                buffer.RemoveAt(0); // 保留最后一个字节，防止丢失部分帧头
                            }
                            break;
                        }

                        // 确保缓冲区中有完整的帧
                        if (buffer.Count < start_idx + frame_size)
                            break;

                        // 提取完整的帧
                        byte[] frame = buffer.GetRange(start_idx, frame_size).ToArray();

                        // 检查帧尾 0xEE 0xFF
                        if (frame[frame_size - 2] == 0xEE && frame[frame_size - 1] == 0xFF)
                        {
                            // 解析计数器
                            long counter = ((long)frame[2] << 56) | ((long)frame[3] << 48) | ((long)frame[4] << 40) | ((long)frame[5] << 32) |
                                           ((long)frame[6] << 24) | ((long)frame[7] << 16) | ((long)frame[8] << 8) | frame[9];
                            Console.WriteLine($"Counter: {counter}");

                            // 解析24个通道数据
                            double[] channel_values = new double[num_channels];
                            double[] channel_values_Cali = new double[num_channels];
                            for (int j = 0; j < num_channels; j++)
                            {
                                int channel_start = 10 + j * 4;
                                channel_values[j] = (frame[channel_start] << 24) | (frame[channel_start + 1] << 16) |
                                                    (frame[channel_start + 2] << 8) | frame[channel_start + 3];
                                channel_values_Cali[j] = channel_values[j];
                                
                                //计算表压
                                channel_values[j] = channel_values[j] - sensorAverages[j];
                            }

                            if (caliFlag)
                            {
                                channels_data_cali.Add(channel_values_Cali);
                                Cnt_Cali += 1;
                                pbar.Value = Cnt_Cali;
                                if (Cnt_Cali == 100)
                                {
                                    isReceiving = false;
                                    serialPort.DataReceived -= new SerialDataReceivedEventHandler(dataReceived);
                                    Cnt_Cali = 0;
                                    caliFlag = false;
                                    btn_Cali.Text = "校准大气压";
                                    btn_Cali.ForeColor = Color.Green;
                                    sensorAverages = CalculateSensorAverages(channels_data_cali);
                                    serialPort.DiscardInBuffer();
                                    return;
                                }
                            }
                            else
                            {
                                channels_data.Add(channel_values);// 保存数据
                                NumOfSample += 1;
                                lb_samplenum.Text = NumOfSample.ToString();
                                lb_sampletime.Text = (NumOfSample * 0.037).ToString();
                                UpdateChart(channel_values);// 更新Chart显示传感器数据
                                if (NumOfSample == 3000)
                                {
                                    isReceiving = false;
                                    serialPort.DataReceived -= new SerialDataReceivedEventHandler(dataReceived);
                                    sampleFlag = false;
                                    btn_Sample_Start.Text = "开始采集";
                                    NumOfSample = 0;
                                }
                            }
                            // 输出数据（可选）
                            Console.WriteLine("Channel Values: " + string.Join(", ", channel_values));
                        }

                        // 移除已处理的帧
                        buffer.RemoveRange(start_idx, frame_size);
                    }

                    textBoxReceive.Text += "接收到数据包\r\n";
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error");
                    textBoxReceive.Text = "";//清空
                }
            }
            else
            {
                MessageBox.Show("请打开某个串口", "错误提示");
            }
        }
        // 函数：计算 channels_data_cali 中每个传感器的平均值，并返回存储平均值的数组
        private double[] CalculateSensorAverages(List<double[]> channels_data_cali)
        {
            int numSensors = 24;  // 假设每个数组有24个传感器数据
            int numEntries = channels_data_cali.Count;  // List 的长度（即 100）

            // 初始化存储传感器总和的数组
            double[] sensorSums = new double[numSensors];

            // 遍历 channels_data_cali 中的每个数据
            foreach (var sensorData in channels_data_cali)
            {
                for (int i = 0; i < numSensors; i++)
                {
                    // 累加每个传感器的数据
                    sensorSums[i] += sensorData[i];
                }
            }

            // 计算每个传感器的平均值，并存入全局变量 sensorAverages
            for (int i = 0; i < numSensors; i++)
            {
                sensorAverages[i] = sensorSums[i] / numEntries;
            }

            // 返回平均值数组
            return sensorAverages;
        }
        private void buttonOpenCloseCom_Click(object sender, EventArgs e)
        {
            if (!serialPort.IsOpen)//串口处于关闭状态
            {
                try
                {

                    string strSerialName = comboBoxCom.SelectedItem.ToString();
                    string strBaudRate = comboBoxBaudRate.SelectedItem.ToString(); //string strBaudRate = comboBoxBaudRate.Text;

                    Int32 iBaudRate = Convert.ToInt32(strBaudRate);

                    serialPort.PortName = strSerialName;//串口号
                    serialPort.BaudRate = iBaudRate;//波特率
                    serialPort.DataBits = 8;//数据位
                    serialPort.StopBits = StopBits.One;
                    serialPort.Parity = Parity.None;
                    serialPort.ReadTimeout = 10000;



                    //打开串口
                    serialPort.Open();

                    //打开串口后设置将不再有效
                    comboBoxCom.Enabled = false;
                    comboBoxBaudRate.Enabled = false;

                    buttonOpenCloseCom.Text = "关闭串口";


                }
                catch (System.Exception ex)
                {
                    MessageBox.Show("Error:" + ex.Message, "Error");

                    return;
                }
            }
            else //串口处于打开状态
            {

                serialPort.Close();//关闭串口
                //串口关闭时设置有效
                comboBoxCom.Enabled = true;
                comboBoxBaudRate.Enabled = true;

                buttonOpenCloseCom.Text = "打开串口";
            }
        }

        private void btn_Sample_Start_Click(object sender, EventArgs e)
        {
            if (serialPort.IsOpen)
            {
                if (!sampleFlag)
                {
                    isReceiving = true;
                    NumOfSample = 0;
                    TimeOfSample = 0;
                    serialPort.DataReceived += new SerialDataReceivedEventHandler(dataReceived);
                    sampleFlag = true;
                    btn_Sample_Start.Text = "停止采集";
                }
                else
                {
                    isReceiving = false;
                    serialPort.DataReceived -= new SerialDataReceivedEventHandler(dataReceived);
                    sampleFlag = false;
                    btn_Sample_Start.Text = "开始采集";
                    if(!(string.IsNullOrEmpty(label2.Text)))
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                        // 创建文件名，包含时间戳
                        string fileName = label2.Text + $"\\Systemlog_{timestamp}.txt";

                        // 调用保存日志的方法
                        SaveLogToFile(fileName);
                    }
                }
            }
        }
        private void SaveLogToFile(string fileName)
        {
            try
            {
                // 获取日志内容
                string logContent = textBoxReceive.Text;

                // 将日志内容保存到文件
                using (StreamWriter writer = new StreamWriter(fileName))
                {
                    writer.Write(logContent);
                }

                // 提示用户保存成功
                MessageBox.Show("Log saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // 提示用户保存失败
                MessageBox.Show($"Error saving log: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button14_Click(object sender, EventArgs e)
        {
            comboBoxCom.Text = "";
            comboBoxCom.Items.Clear();

            string[] str = SerialPort.GetPortNames();
            if (str == null)
            {
                MessageBox.Show("本机没有串口！", "Error");
                return;
            }
            //添加串口
            foreach (string s in str)
            {
                comboBoxCom.Items.Add(s);
            }
            //设置默认串口选项
            comboBoxCom.SelectedIndex = -1;
        }

        private void btn_Save_Click(object sender, EventArgs e)
        {
            // 创建文件对话框
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "CSV files (*.csv)|*.csv";
            saveFileDialog.Title = "Save sensor data as CSV";

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                // 获取文件路径
                string filePath = saveFileDialog.FileName;

                // 调用方法将 channels_data 保存为 CSV
                SaveDataToCSV(filePath);
            }
        }

        private void SaveDataToCSV(string filePath)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    // 写入CSV文件头，假设有24个通道
                    writer.WriteLine(string.Join(",", Enumerable.Range(1, num_channels).Select(i => $"Channel {i}")));

                    // 写入每一行的数据
                    foreach (var channelValues in channels_data)
                    {
                        writer.WriteLine(string.Join(",", channelValues));
                    }
                }

                MessageBox.Show("Data saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving data: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btn_Cali_Click(object sender, EventArgs e)
        {
            if (serialPort.IsOpen)
            {
                if (!caliFlag)
                {
                    isReceiving = true;
                    caliFlag = true;
                    btn_Cali.Text = "校准中";
                    btn_Cali.ForeColor = Color.Red;
                    serialPort.DataReceived += new SerialDataReceivedEventHandler(dataReceived);
                }
            }
        }

        private void btn_ClearData_Click(object sender, EventArgs e)
        {
            channels_data.Clear();
            foreach (var series in crt_DataLoger.Series)
            {
                series.Points.Clear();  // 清空每个系列的数据
            }
        }
    }
}
