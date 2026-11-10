namespace BackgroundJobs.Demo.Jobs;

public static class DemoJobValidation
{
    public static void ValidateSucceedAfter(SucceedAfterArgs args)
    {
        if (args is null || args.DelayMs is < 0 or > 60000)
            throw new JobValidationException("delayMs deve ser um inteiro de 0 a 60000.");
    }

    public static void ValidateFailNTimes(FailNTimesArgs args)
    {
        if (args is null || args.FailTimes is < 0 or > 10)
            throw new JobValidationException("failTimes deve ser um inteiro de 0 a 10.");
    }

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
