using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using ZapMQ;

namespace sample
{
    public partial class Form2 : Form
    {
        private ZapMQWrapper zapMQWrapper;
        public Form2()
        {
            InitializeComponent();
            zapMQWrapper = new ZapMQWrapper("localhost", 5679);
            zapMQWrapper.OnRPCExpired = RPCExpired;
            SetRichTextBox("*** ZapMQ Wrapper Started ***");
        }
        public void RPCExpired(ZapJSONMessage pMessage)
        {
            SetRichTextBox("*** RPC Message Expired ***");
            SetRichTextBox("Id:" + pMessage.Id);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            richTextBox1.Clear();
        }

        private void Form2_FormClosed(object sender, FormClosedEventArgs e)
        {
            zapMQWrapper.StopThreads();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            MyJsonObject message = new MyJsonObject();
            message.message = "message to send";
            if (zapMQWrapper.SendMessage(textBox2.Text, message, Convert.ToInt32(textBox3.Text)))
            {
                SetRichTextBox("*** Message Sended ***");
            }
            else
            {
                SetRichTextBox("*** Error to Send Message ***");
            }
        }
        private void button3_Click(object sender, EventArgs e)
        {
            MyJsonObject message = new MyJsonObject();
            message.message = "RPC message to send";
            if (zapMQWrapper.SendRPCMessage(textBox2.Text, message, zapMQHandlerRPC, Convert.ToInt32(textBox3.Text)))
            {
                SetRichTextBox("*** Message Sended ***");
            }
            else
            {
                SetRichTextBox("*** Error to Send Message ***");
            }
        }
        public void zapMQHandlerRPC(ZapJSONMessage pMessage)
        {
            SetRichTextBox("*** RPC Answer ***");
            SetRichTextBox(pMessage.Response.ToString());
        }
        private void button4_Click(object sender, EventArgs e)
        {
            zapMQWrapper.Bind(textBox4.Text, zapMQHandler);
            listBox1.Items.Add(textBox4.Text);
            SetRichTextBox("*** Binded in " + textBox4.Text + " ***");
        }
        public object zapMQHandler(ZapJSONMessage pMessage, out bool pProcessing)
        {
            SetRichTextBox("*** Processing Message ***");
            Thread.Sleep(Convert.ToInt32(textBox1.Text));
            SetRichTextBox(pMessage.Body.ToString());
            pProcessing = false;
            SetRichTextBox("*** Message Processed ***");
            return null;
        }
        public object zapMQHandlerRPCMessage(ZapJSONMessage pMessage, out bool pProcessing)
        {
            SetRichTextBox("*** Processing Message ***");
            Thread.Sleep(Convert.ToInt32(textBox1.Text));
            SetRichTextBox(pMessage.Body.ToString());
            pProcessing = false;
            SetRichTextBox("*** Message Processed ***");
            MyJsonObject message = new MyJsonObject();
            message.message = @"Teste de envio com \ ã ó ç";
            return message;
        }

        private void button5_Click(object sender, EventArgs e)
        {
            zapMQWrapper.Bind(textBox4.Text, zapMQHandlerRPCMessage);
            listBox1.Items.Add(textBox4.Text);
            SetRichTextBox("*** Binded in " + textBox4.Text + " ***");
        }

        private void button6_Click(object sender, EventArgs e)
        {
            zapMQWrapper.Bind(textBox4.Text, zapMQHandlerNewPublish);
            listBox1.Items.Add(textBox4.Text);
            SetRichTextBox("*** Binded in " + textBox4.Text + " ***");
        }
        public object zapMQHandlerNewPublish(ZapJSONMessage pMessage, out bool pProcessing)
        {
            SetRichTextBox("*** Processing Message ***");
            Thread.Sleep(Convert.ToInt32(textBox1.Text));
            SetRichTextBox(pMessage.ToJSON());
            pProcessing = false;
            SetRichTextBox("*** Message Processed ***");
            MyJsonObject message = new MyJsonObject();
            message.message = "New Publish";
            zapMQWrapper.SendMessage("Teste", message, Convert.ToInt32(textBox3.Text));
            return null;
        }

        private void button7_Click(object sender, EventArgs e)
        {
            zapMQWrapper.UnBind(textBox4.Text);
            listBox1.Items.Remove(textBox4.Text);
            SetRichTextBox("*** UnBinded in "+ textBox4.Text +" ***");
        }
        public void SetRichTextBox(string text)
        {
            if (InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate () { richTextBox1.AppendText(text + "\n"); });
                return;
            }
            richTextBox1.AppendText(text + "\n");
        }
    }
}
