namespace Codeium_Security.Frontend.Models;

public class AnalysisStepModel
{
    public string Nom { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StepStatus Status { get; set; } = StepStatus.Attente;
}
