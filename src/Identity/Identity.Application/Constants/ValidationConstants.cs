namespace Haworks.Identity.Application.Constants;

public static class ValidationConstants
{
    public static class Email
    {
        public const int MaxLength = 254;
    }

    public static class Name
    {
        public const int MaxLength = 100;
        public const int MinLength = 1;
    }

    public static class Address
    {
        public const int MaxStreetLength = 500;
        public const int MaxCityLength = 100;
        public const int MaxStateLength = 100;
        public const int MaxPostalCodeLength = 20;
        public const int MaxCountryLength = 100;
    }

    public static class Phone
    {
        public const int MaxLength = 30;
        public const int MinLength = 7;
    }

    public static class Password
    {
        public const int MinLength = 8;
        public const int MaxLength = 128;
        public const string UppercasePattern = @"[A-Z]";
        public const string LowercasePattern = @"[a-z]";
        public const string DigitPattern = @"\d";
        public const string SpecialCharPattern = @"[!@#$%^&*()_+\-=\[\]{};':""\|,.<>\/?]";
    }

    public static class Username
    {
        public const int MinLength = 3;
        public const int MaxLength = 50;
    }
}
