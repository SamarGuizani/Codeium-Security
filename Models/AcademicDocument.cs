namespace Codeium_Security.Models
{
    public class AcademicDocument
    {
        public string StudentName { get; set; } = "";
        public string Cin { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string University { get; set; } = "";
        public string MasterTitle { get; set; } = "";
        public string Parcours { get; set; } = "";
        public string Level { get; set; } = "";
        public string AcademicYear { get; set; } = "";
        public decimal? Average { get; set; }
       
    }
}
