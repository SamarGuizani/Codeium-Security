namespace Codeium_Security.Models
{
    public class AcademicDocument
    {
        public string StudentName { get; set; } = "";
        public string University { get; set; } = "";
        public string Semester { get; set; } = "";
        public decimal? Average { get; set; }
    }
}