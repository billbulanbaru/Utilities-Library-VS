﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Pipes;
using System.IO;
using System.Xml.Serialization;
using System.Xml;


namespace Chetch.Utilities
{
    public static class NamedPipeManager
    {
        public const int CLOSE_PIPE = 1;
        public const int WAIT_FOR_NEXT_CONNECTION = 2;
        public const int SECURITY_EVERYONE = 1;
        public const int DEFAULT_BUFFER_IN = 4096;
        public const int DEFAULT_BUFFER_OUT = 4096;

        public struct PipeInfo
        {
            public String Name { get; set; }
            public PipeDirection Direction { get; set; }
            public PipeSecurity Security { get; set; }
            public PipeStream Stream { get; set; }
            public Func<PipeInfo, int> OnClientConnect { get; set; }
        }

        public class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }

        public enum MessageType
        {
            NOT_SET,
            REGISTER_LISTENER,
            CUSTOM,
            INFO,
            WARNING,
            ERROR,
            PING,
            PING_RESPONSE,
            STATUS_REQUEST,
            STATUS_RESPONSE,
            COMMAND,
            ERROR_TEST,
            ECHO,
            ECHO_RESPONSE
        }

        [Serializable]
        public class MessageValue
        {
            public String Key;
            public Object Value;
        }

        [Serializable]
        public class Message
        {
            public String ID;
            public String Target; //to help routing to the correct place at the receive end
            public String ResponseID; //normally the ID of the message that was sent requesting a response (e.g. Ping and Ping Response)
            public String Sender; //normally the name of the 'inbound' pipe that will be listening for responses (this can be different from the 'outbound' pipe that the message is being sent down)
            public MessageType Type;
            public int SubType;
            public List<MessageValue> Values = new List<MessageValue>();
            public String Value
            {
                get
                {
                    return Values.Count > 0 ? GetString("Value") : null;
                }
                set
                {
                    AddValue("Value", value);
                }
            }

            public Message()
            {
                ID = CreateID();
                Type = MessageType.NOT_SET;
            }

            public Message(MessageType type = MessageType.NOT_SET)
            {
                ID = CreateID();
                Type = type;
            }

            public Message(String message, int subType = 0, MessageType type = MessageType.NOT_SET)
            {
                ID = CreateID();
                Value = message;
                SubType = subType;
                Type = type;
            }

            public Message(String message, MessageType type = MessageType.NOT_SET) : this(message, 0, type)
            {
                //empty
            }

            private String CreateID()
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id.ToString() + "-" + this.GetHashCode() + "-" + DateTime.Now.ToString("yyyyMMddHHmmssffff");
            }

            public void AddValue(String key, Object value)
            {
                var key2cmp = key.ToLower();
                foreach (var v in Values)
                {
                    if (v.Key.ToLower() == key2cmp)
                    {
                        v.Value = value;
                        return;
                    }
                }

                //if here then there is no existing value
                var mv = new MessageValue();
                mv.Key = key;
                mv.Value = value;
                Values.Add(mv);
            }

            public Object GetValue(String key)
            {
                var key2cmp = key.ToLower();
                foreach (var v in Values)
                {
                    if (v.Key.ToLower() == key2cmp)
                    {
                        return v.Value;
                    }
                }
                throw new Exception("No value found for key " + key);
            }

            public String GetString(String key)
            {
                return (String)GetValue(key);
            }

            public void Clear()
            {
                Values.Clear();
            }

            public String GetQueryString()
            {
                Dictionary<String, Object> vals = new Dictionary<String, Object>();
                vals.Add("ID", ID);
                vals.Add("ResonseID", ResponseID);
                vals.Add("Target", Target);
                vals.Add("Sender", Sender);
                vals.Add("Type", Type);
                vals.Add("SubType", SubType);
                foreach(var mv in Values)
                {
                    vals.Add(mv.Key, mv.Value);
                }
                return Convert.ToQueryString(vals);
            }
            
            public String GetXML()
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Indent = false;
                settings.NewLineHandling = NewLineHandling.None;

                String xmlStr;
                using (StringWriter stringWriter = new Utf8StringWriter())
                {
                    using (XmlWriter xmlWriter = XmlWriter.Create(stringWriter, settings))
                    {
                        XmlSerializer serializer = new XmlSerializer(this.GetType());
                        serializer.Serialize(xmlWriter, this); //, namespaces);
                        xmlStr = stringWriter.ToString();
                        xmlWriter.Close();
                    }

                    stringWriter.Close();
                }
                return xmlStr;
            }

            public void Serialize(StreamWriter stream)
            {
                var xmlStr = GetXML();
                stream.WriteLine(xmlStr);
            }

            public static T Deserialize<T>(String s)
            {
                byte[] byteArray = Encoding.UTF8.GetBytes(s);
                var stream = new MemoryStream(byteArray);
                var writer = new StreamWriter(stream);
                writer.Write(s);
                writer.Flush();
                stream.Position = 0;

                var serializer = new XmlSerializer(typeof(T));
                return (T)serializer.Deserialize(stream);
            }


            virtual protected String ToStringHeader()
            {
                String lf = Environment.NewLine;
                String s = "ID: " + ID + lf;
                s += "Target: " + Target + lf;
                s += "Response ID: " + ResponseID + lf;
                s += "Sender: " + Sender + lf;
                s += "Type: " + Type;
                return s;
            }

            virtual protected String ToStringValues()
            {
                String lf = Environment.NewLine;
                String s = "Values: " + lf;
                foreach (var v in Values)
                {
                    s += v.Key + " = " + v.Value + lf;
                }

                return s;
            }

            override public String ToString()
            {
                String lf = Environment.NewLine;
                String s = ToStringHeader();
                s += lf + ToStringValues();
                return s;
            }
        }

        public static PipeSecurity GetSecurity(int securityMode)
        {
            PipeSecurity security = new PipeSecurity();

            switch (securityMode)
            {
                case SECURITY_EVERYONE:
                    PipeAccessRule psEveryone = new PipeAccessRule("Everyone", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow);
                    security.AddAccessRule(psEveryone);
                    break;
            }

            return security;
        }

        public static NamedPipeServerStream Create(String pipeName, PipeDirection direction, PipeSecurity security, Func<PipeInfo, int> OnClientConnect, int maxServerInstances = 1, int inBufferSize = DEFAULT_BUFFER_IN, int outBufferSize = DEFAULT_BUFFER_OUT)
        {
            NamedPipeServerStream pipeServer = new NamedPipeServerStream(pipeName,
                                                                        direction,
                                                                        maxServerInstances,
                                                                        PipeTransmissionMode.Byte,
                                                                        PipeOptions.Asynchronous,
                                                                        inBufferSize,
                                                                        outBufferSize,
                                                                        security);
            if (pipeServer != null)
            {
                var pipeInfo = new PipeInfo();
                pipeInfo.Name = pipeName;
                pipeInfo.Direction = direction;
                pipeInfo.Security = security;
                pipeInfo.Stream = pipeServer;
                pipeInfo.OnClientConnect = OnClientConnect;
         
                pipeServer.BeginWaitForConnection(new AsyncCallback(WaitForClientConnection), pipeInfo);
            }

            return pipeServer;
        }

        static void WaitForClientConnection(IAsyncResult result)
        {
            PipeInfo pipeInfo = (PipeInfo)result.AsyncState;
            NamedPipeServerStream pipeServer = (NamedPipeServerStream)pipeInfo.Stream;
            int ret = pipeInfo.OnClientConnect != null ? WAIT_FOR_NEXT_CONNECTION : CLOSE_PIPE;

            // End waiting for the connection
            try
            {
                pipeServer.EndWaitForConnection(result);
                //do delegate function here
                if (pipeInfo.OnClientConnect != null && pipeServer.IsConnected)
                {
                    ret = pipeInfo.OnClientConnect(pipeInfo);
                }
            }
            catch (Exception)
            {
                ret = CLOSE_PIPE;
            }
            

            switch (ret)
            {
                case CLOSE_PIPE:
                    pipeServer.Close();
                    pipeServer.Dispose();
                    pipeServer = null;
                    break;

                case WAIT_FOR_NEXT_CONNECTION:
                    if (pipeServer.IsConnected)
                    {
                        try
                        {
                            pipeServer.BeginWaitForConnection(new AsyncCallback(WaitForClientConnection), pipeInfo);
                        }
                        catch (IOException)
                        {
                            pipeServer = Create(pipeInfo.Name, pipeInfo.Direction, pipeInfo.Security, pipeInfo.OnClientConnect);
                        }
                    }
                    else
                    {
                        pipeServer.Close();
                        pipeServer.Dispose();
                        pipeServer = null;
                        pipeServer = Create(pipeInfo.Name, pipeInfo.Direction, pipeInfo.Security, pipeInfo.OnClientConnect);

                    }
                    break;
            } //end switch
        }
    }
}
