namespace PlannerBot.Data;

public enum Availability
{
    Yes,
    No,
    Probably,
    Unknown
}

public static class AvailabilityExtensions
{
    extension(Availability availability)
    {
        public string ToSign()
        {
            return availability switch
            {
                Availability.Yes => "+",
                Availability.No => "-",
                Availability.Probably => "?",
                Availability.Unknown => "???",
                _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, null)
            };
        }
    }
}