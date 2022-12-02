namespace DucoboxSilentSerial
{
    public class Thing
    {
        public string Id { get; set; }

        public string Description { get; set; }

        public List<Thing> Things { get; set; }

        public string? MeasurementUnit { get; set; }

        public List<object> MeasurementPossibleValues { get; set; }

        public List<Measurement> Measurements { get; set; }

        public Thing(
            string id,
            string description)
        {
            Id = id;
            Description = description;
            Things = new List<Thing>();
            Measurements = new List<Measurement>(); 
            MeasurementPossibleValues = new List<object>();
        }
    }
}