using System;

namespace erl.AspNetCore.AgsToken
{
    public class AgsTokenResponse
    {
        public string token { get; set; }
        public long expires { get; set; }

        public bool IsValid()
        {
            if (FromUnixTime(expires) < DateTime.Now)
            {
                return false;
            }

            return true;
        }

        private DateTime FromUnixTime(long unixTime)
            => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(unixTime);
    }

    
}