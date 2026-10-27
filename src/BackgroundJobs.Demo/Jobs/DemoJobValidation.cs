namespace BackgroundJobs.Demo.Jobs;

public static class DemoJobValidation
{
    public static void ValidateLogMessage(LogMessageArgs args)
    {
        if (args is null || args.Message is null)
        {
            throw new JobValidationException("message é obrigatório.");
        }

        if (args.Message.Length > 200)
        {
            throw new JobValidationException("message deve ter até 200 unidades UTF-16.");
        }
    }
}
