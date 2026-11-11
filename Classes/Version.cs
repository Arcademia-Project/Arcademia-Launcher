using System;

namespace ArcademiaGameLauncher.Classes
{
    struct Version : IComparable<Version>, IComparable
    {
        // Zero value for the Version struct
        internal static Version zero = new(0, 0, 0);

        public int major;
        public int minor;
        public int subMinor;

        internal Version(short _major, short _minor, short _subMinor)
        {
            // Initialize the version number
            major = _major;
            minor = _minor;
            subMinor = _subMinor;
        }

        internal Version(string version)
        {
            string[] parts = version.Split('.');

            // Reset the version number if it is not in the correct format
            if (parts.Length != 3)
            {
                major = 0;
                minor = 0;
                subMinor = 0;
                return;
            }

            // Parse the version number
            major = int.Parse(parts[0]);
            minor = int.Parse(parts[1]);
            subMinor = int.Parse(parts[2]);
        }

        internal readonly bool IsDifferentVersion(Version _otherVersion)
        {
            // Compare each part of the version number
            if (major != _otherVersion.major)
                return true;
            else if (minor != _otherVersion.minor)
                return true;
            else if (subMinor != _otherVersion.subMinor)
                return true;
            else
                return false;
        }

        public override readonly string ToString()
        {
            // Return the version number as a string
            return $"{major}.{minor}.{subMinor}";
        }

        public readonly int CompareTo(Version other)
        {
            var c = major.CompareTo(other.major);
            if (c != 0)
                return c;
            c = minor.CompareTo(other.minor);
            if (c != 0)
                return c;
            return subMinor.CompareTo(other.subMinor);
        }

        readonly int IComparable.CompareTo(object obj)
        {
            if (obj is not Version)
                throw new ArgumentException("Can only compare to another Version");
            return CompareTo((Version)obj);
        }
    }
}
