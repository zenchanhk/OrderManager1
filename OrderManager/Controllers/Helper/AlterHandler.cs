using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;

using System.Threading.Tasks;
using System.IO;

namespace AmiBroker.Controllers
{
    public class AlterHandler
    {
        //Telegram API key
        private static readonly string APIKey = "900612522:AAFQNxuk-0RI5G0NzX9oTHwlus8UsCtxI2Y";
        private static readonly string chatId = "-1001431725334";
        public static bool SendMessage(string msg)
        {
            string url = $"https://api.telegram.org/bot{APIKey}/sendMessage?chat_id={chatId}&text={msg}";
            WebClient webclient = new WebClient();
            string responseText = webclient.DownloadString(url);
            var obj = JToken.Parse(responseText);
            return (bool)obj.SelectToken("ok").ToObject(typeof(bool));
        }
    }
}
