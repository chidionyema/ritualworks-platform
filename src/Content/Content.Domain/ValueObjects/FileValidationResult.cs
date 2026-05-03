namespace Haworks.Content.Domain.ValueObjects
{
    /// <summary>
    /// Represents the result of a file validation operation.
    /// </summary>
    public record FileValidationResult
    {
        /// <summary>
        /// Gets the determined or claimed file type.
        /// For successful validations, this is guaranteed to be non-null.
        /// For failures, this can be null if the type could not be determined or is irrelevant.
        /// </summary>
        public string? FileType { get; }

        /// <summary>
        /// Gets the collection of validation errors. Empty if validation is successful.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>
        /// Gets a value indicating whether the validation was successful.
        /// </summary>
        public bool IsValid => !Errors.Any();

        private FileValidationResult(string? fileType, IEnumerable<string>? errors)
        {
            FileType = fileType;
            Errors = errors?.ToList().AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>
        /// Creates a success validation result.
        /// </summary>
        public static FileValidationResult Success(string fileType)
        {
            if (string.IsNullOrWhiteSpace(fileType))
            {
                throw new ArgumentException("FileType cannot be null or whitespace for a successful validation.", nameof(fileType));
            }
            return new FileValidationResult(fileType, null);
        }

        /// <summary>
        /// Creates a failure validation result with a collection of errors.
        /// </summary>
        public static FileValidationResult Failure(IEnumerable<string> errors, string? fileType = null)
        {
            if (errors == null || !errors.Any())
            {
                throw new ArgumentException("Errors collection cannot be null or empty for a failure result.", nameof(errors));
            }
            return new FileValidationResult(fileType, errors);
        }

        /// <summary>
        /// Creates a failure validation result with a single error.
        /// </summary>
        public static FileValidationResult Failure(string error, string? fileType = null)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("Error message cannot be null or whitespace.", nameof(error));
            }
            return new FileValidationResult(fileType, new[] { error });
        }
    }
}
