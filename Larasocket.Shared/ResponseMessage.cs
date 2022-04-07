using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;

namespace Larasocket.Shared
{
    //
    // Summary:
    //     Received message, could be Text or Binary
    public class ResponseMessage
    {
        //
        // Summary:
        //     Received text message (only if type = WebSocketMessageType.Text)
        public string Text
        {
            get;
        }

        //
        // Summary:
        //     Received text message (only if type = WebSocketMessageType.Binary)
        public byte[] Binary
        {
            get;
        }

        //
        // Summary:
        //     Current message type (Text or Binary)
        public WebSocketMessageType MessageType
        {
            get;
        }

        private ResponseMessage(byte[] binary, string text, WebSocketMessageType messageType)
        {
            Binary = binary;
            Text = text;
            MessageType = messageType;
        }

        //
        // Summary:
        //     Return string info about the message
        public override string ToString()
        {
            if (MessageType == WebSocketMessageType.Text)
            {
                return Text;
            }

            return $"Type binary, length: {Binary?.Length}";
        }

        //
        // Summary:
        //     Create text response message
        public static ResponseMessage TextMessage(string data)
        {
            return new ResponseMessage(null, data, WebSocketMessageType.Text);
        }

        //
        // Summary:
        //     Create binary response message
        public static ResponseMessage BinaryMessage(byte[] data)
        {
            return new ResponseMessage(data, null, WebSocketMessageType.Binary);
        }
    }
}
