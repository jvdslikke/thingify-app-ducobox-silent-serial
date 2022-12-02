namespace DucoboxSilentSerial
{
    public class Measurement
    {
        public DateTime DateTime { get; set; }

        public object Value { get; set; }

        public Measurement(DateTime dateTime, object value)
        {
            DateTime = dateTime;
            Value = value;
        }
    }
}