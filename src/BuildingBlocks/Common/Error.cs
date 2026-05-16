namespace Haworks.BuildingBlocks.Common;

/// <summary>
/// Categorizes the type of error for HTTP status code mapping.
/// </summary>
public enum ErrorType
{
    None,
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Timeout,
    Internal,
    Unauthorized
}

/// <summary>
/// Represents an error with a code, message, and type.
/// Used throughout the application for type-safe error handling.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Internal)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    // Factory methods for creating errors dynamically
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Storage(string code, string message) => new(code, message, ErrorType.Internal);
    public static Error Database(string code, string message) => new(code, message, ErrorType.Internal);
    public static Error Internal(string code, string message) => new(code, message, ErrorType.Internal);
    public static Error Timeout(string code, string message) => new(code, message, ErrorType.Timeout);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static class Payment
    {
        public static readonly Error DuplicateOrder = new("Payment.Duplicate", "Duplicate order detected");
        public static readonly Error InsufficientStock = new("Payment.InsufficientStock", "Insufficient stock for one or more products");
        public static readonly Error SessionNotFound = new("Payment.SessionNotFound", "Payment session not found");
        public static readonly Error ProductNotFound = new("Payment.ProductNotFound", "Product not found");
        public static readonly Error InvalidSession = new("Payment.InvalidSession", "Invalid or expired payment session");
        public static readonly Error PaymentFailed = new("Payment.Failed", "Payment processing failed");
        public static readonly Error OrderNotFound = new("Payment.OrderNotFound", "Order not found");
        public static readonly Error ProcessingError = new("Payment.ProcessingError", "An error occurred while processing the payment");
        public static readonly Error SessionCreationFailed = new("Payment.SessionCreationFailed", "Order created but payment session failed to initialize");
    }

    public static class ValidationErrors
    {
        public static readonly Error InvalidEmail = new("Validation.InvalidEmail", "Invalid email format");
        public static readonly Error RequiredField = new("Validation.Required", "Field is required");
        public static readonly Error InvalidQuantity = new("Validation.InvalidQuantity", "Quantity must be greater than zero");
        public static readonly Error TooManyItems = new("Validation.TooManyItems", "Too many items in order");
    }

    public static class Auth
    {
        public static readonly Error InvalidCredentials = new("Auth.InvalidCredentials", "Invalid username or password", ErrorType.Unauthorized);
        public static readonly Error TokenExpired = new("Auth.TokenExpired", "Token has expired", ErrorType.Unauthorized);
        public static readonly Error TokenRevoked = new("Auth.TokenRevoked", "Token has been revoked", ErrorType.Unauthorized);
        public static readonly Error Unauthorized = new("Auth.Unauthorized", "User is not authorized", ErrorType.Unauthorized);
        public static readonly Error UserNotFound = new("Auth.UserNotFound", "User not found", ErrorType.Unauthorized);
        public static readonly Error MissingTokens = new("Auth.MissingTokens", "Access and refresh tokens are required.", ErrorType.Validation);
        public static readonly Error InvalidAccessToken = new("Auth.InvalidAccessToken", "Invalid access token structure.", ErrorType.Unauthorized);
        public static readonly Error MissingUserId = new("Auth.MissingUserId", "Token claims are missing user identification.", ErrorType.Unauthorized);
        public static readonly Error InvalidRefreshToken = new("Auth.InvalidRefreshToken", "Session invalid. Please log in again.", ErrorType.Unauthorized);
        public static readonly Error TokenProcessingError = new("Auth.TokenProcessingError", "Invalid token signature.", ErrorType.Unauthorized);
        public static readonly Error RefreshFailed = new("Auth.RefreshFailed", "An internal error occurred.", ErrorType.Internal);
        // Surfaced as 400 (not 404) so callers attempting to unlink a provider
        // they don't have a login for get a clear bad-request response rather
        // than an ambiguous resource-not-found.
        public static readonly Error LoginNotFound = new("Auth.LoginNotFound", "External login not found.", ErrorType.Validation);
        public static readonly Error LinkFailed = new("Auth.LinkFailed", "Unable to link external login.", ErrorType.Internal);
        public static readonly Error UnlinkFailed = new("Auth.UnlinkFailed", "Unable to unlink external login.", ErrorType.Internal);
        public static readonly Error RegistrationFailed = new("Auth.RegistrationFailed", "Registration failed.", ErrorType.Validation);
        public static readonly Error RoleAssignmentFailed = new("Auth.RoleAssignmentFailed", "Role assignment failed.", ErrorType.Internal);
        public static readonly Error ClaimAssignmentFailed = new("Auth.ClaimAssignmentFailed", "Claim assignment failed.", ErrorType.Internal);
        public static readonly Error InvalidProviderKey = new("Auth.InvalidProviderKey", "External login information is incomplete.", ErrorType.Validation);
        public static readonly Error InvalidContext = new("Auth.InvalidContext", "HTTP context is required.", ErrorType.Validation);
        public static readonly Error ExternalLoginFailed = new("Auth.ExternalLoginFailed", "Unable to retrieve external login information.", ErrorType.Validation);
        public static readonly Error MissingEmail = new("Auth.MissingEmail", "Email is required but not provided by the external login.", ErrorType.Validation);
        public static readonly Error UnverifiedEmail = new("Auth.UnverifiedEmail", "Email must be verified by the external provider before linking accounts.", ErrorType.Validation);
        public static readonly Error AlreadyLinked = new("Auth.AlreadyLinked", "This external account is already connected to another user.", ErrorType.Conflict);
        public static readonly Error UserInconsistency = new("Auth.UserInconsistency", "Account inconsistency detected.", ErrorType.Internal);
        public static readonly Error CreateFailed = new("Auth.CreateFailed", "Could not create a new account.", ErrorType.Internal);

        public static readonly Error AccountDeactivated = new("Auth.AccountDeactivated", "Account is deactivated.", ErrorType.Forbidden);

        /// <summary>
        /// Creates an error indicating the account is locked out.
        /// </summary>
        /// <param name="lockoutMinutes">Duration of lockout in minutes.</param>
        public static Error AccountLocked(int lockoutMinutes) =>
            new("Auth.AccountLocked", $"Account is locked. Please try again after {lockoutMinutes} minutes.", ErrorType.Forbidden);
    }

    public static class Content
    {
        public static readonly Error FileNotFound = new("Content.FileNotFound", "File not found");
        public static readonly Error InvalidFileType = new("Content.InvalidFileType", "Invalid file type");
        public static readonly Error FileTooLarge = new("Content.FileTooLarge", "File exceeds maximum size");
        public static readonly Error UploadFailed = new("Content.UploadFailed", "File upload failed");
        public static readonly Error VirusDetected = new("Content.VirusDetected", "Virus detected in file");
        public static readonly Error NotFound = new("Content.NotFound", "Content not found.", ErrorType.NotFound);
        public static readonly Error SessionNotFound = new("Content.SessionNotFound", "Upload session not found.", ErrorType.NotFound);
        public static readonly Error EmptyFile = new("Content.EmptyFile", "No file provided or file is empty.", ErrorType.Validation);
        public static readonly Error ValidationFailed = new("Content.ValidationFailed", "File validation failed.", ErrorType.Validation);
        public static readonly Error CompletionFailed = new("Content.CompletionFailed", "Failed to complete upload session.", ErrorType.Internal);
        public static readonly Error InvalidOperation = new("Content.InvalidOperation", "Invalid operation.", ErrorType.Validation);
        public static readonly Error Forbidden = new("Content.Forbidden", "Access denied.", ErrorType.Forbidden);
        public static readonly Error InvalidChunk = new("Content.InvalidChunk", "Invalid or empty chunk file.", ErrorType.Validation);
        public static readonly Error InvalidChunkIndex = new("Content.InvalidChunkIndex", "Chunk index cannot be negative.", ErrorType.Validation);
        public static readonly Error ChunkOutOfRange = new("Content.ChunkOutOfRange", "Chunk index out of range.", ErrorType.Validation);
        public static readonly Error StreamError = new("Content.StreamError", "Stream processing error.", ErrorType.Internal);
        public static readonly Error InvalidChunkParams = new("Content.InvalidChunkParams", "Invalid chunk parameters.", ErrorType.Validation);
        public static readonly Error MetadataValidationFailed = new("Content.MetadataValidationFailed", "Metadata validation failed.", ErrorType.Validation);
        public static readonly Error InvalidArgument = new("Content.InvalidArgument", "Invalid argument.", ErrorType.Validation);
    }

    public static class Vault
    {
        public static readonly Error ConnectionFailed = new("Vault.ConnectionFailed", "Failed to connect to Vault");
        public static readonly Error CredentialRefreshFailed = new("Vault.CredentialRefreshFailed", "Failed to refresh credentials");
        public static readonly Error CertificateValidationFailed = new("Vault.CertificateValidationFailed", "Certificate validation failed");
    }

    public static class Orders
    {
        public static readonly Error NotFound = new("Orders.NotFound", "Order not found.", ErrorType.NotFound);
        public static readonly Error Forbidden = new("Orders.Forbidden", "You are not authorized to view this order.", ErrorType.Forbidden);
        public static readonly Error NoItems = new("Orders.NoItems", "No items provided for checkout.", ErrorType.Validation);
        public static readonly Error MissingGuestInfo = new("Orders.MissingGuestInfo", "Guest checkout requires shipping information.", ErrorType.Validation);
        public static readonly Error IncompleteGuestInfo = new("Orders.IncompleteGuestInfo", "Incomplete guest information provided.", ErrorType.Validation);
        public static readonly Error InvalidEmail = new("Orders.InvalidEmail", "Invalid email format.", ErrorType.Validation);
        public static readonly Error InvalidUser = new("Orders.InvalidUser", "User ID is required.", ErrorType.Validation);
        public static readonly Error InvalidRequest = new("Orders.InvalidRequest", "Invalid request parameters.", ErrorType.Validation);
        public static readonly Error InvalidSession = new("Orders.InvalidSession", "Invalid session ID.", ErrorType.Validation);
        public static readonly Error PaymentNotFound = new("Orders.PaymentNotFound", "Payment record not found.", ErrorType.NotFound);
        public static readonly Error OrderNotFound = new("Orders.OrderNotFound", "Order not found for the provided session.", ErrorType.NotFound);
        public static readonly Error RetrievalFailed = new("Orders.RetrievalFailed", "An error occurred while retrieving the order.", ErrorType.Internal);
        public static readonly Error VerificationFailed = new("Orders.VerificationFailed", "An error occurred while verifying the order.", ErrorType.Internal);
        public static readonly Error FetchFailed = new("Orders.FetchFailed", "Failed to fetch order.", ErrorType.Internal);

        // Factory methods for dynamic messages
        public static Error NotFoundWithId(Guid orderId) =>
            new("Orders.NotFound", $"Order with ID {orderId} not found.", ErrorType.NotFound);
    }

    public static class Reviews
    {
        public static readonly Error NotFound = new("Reviews.NotFound", "Review not found.", ErrorType.NotFound);
        public static readonly Error ProductNotFound = new("Reviews.ProductNotFound", "Product not found.", ErrorType.NotFound);
        public static readonly Error InvalidProduct = new("Reviews.InvalidProduct", "Review does not belong to the specified product.", ErrorType.Validation);
        public static readonly Error Forbidden = new("Reviews.Forbidden", "You are not authorized to modify this review.", ErrorType.Forbidden);
        public static readonly Error CreateFailed = new("Reviews.CreateFailed", "Failed to create review.", ErrorType.Internal);
        public static readonly Error UpdateFailed = new("Reviews.UpdateFailed", "Failed to update review.", ErrorType.Internal);
        public static readonly Error DeleteFailed = new("Reviews.DeleteFailed", "Failed to delete review.", ErrorType.Internal);
        public static readonly Error ApproveFailed = new("Reviews.ApproveFailed", "Failed to approve review.", ErrorType.Internal);
        public static readonly Error FetchFailed = new("Reviews.FetchFailed", "Failed to fetch reviews.", ErrorType.Internal);

        // Factory methods for dynamic messages
        public static Error ProductNotFoundWithId(Guid productId) =>
            new("Reviews.ProductNotFound", $"Product with ID {productId} not found.", ErrorType.NotFound);
    }

    public static class Categories
    {
        public static readonly Error NotFound = new("Categories.NotFound", "Category not found.", ErrorType.NotFound);
        public static readonly Error InvalidName = new("Categories.InvalidName", "Category name is required.", ErrorType.Validation);

        // Factory methods for dynamic messages
        public static Error NotFoundWithId(Guid categoryId) =>
            new("Categories.NotFound", $"Category with ID {categoryId} not found.", ErrorType.NotFound);
    }

    public static class Products
    {
        public static readonly Error NotFound = new("Products.NotFound", "Product not found.", ErrorType.NotFound);
        public static readonly Error InvalidCategory = new("Products.InvalidCategory", "Invalid category ID.", ErrorType.Validation);
        public static readonly Error CreateFailed = new("Products.CreateFailed", "Failed to create product.", ErrorType.Internal);
        public static readonly Error UpdateFailed = new("Products.UpdateFailed", "Failed to update product.", ErrorType.Internal);
        public static readonly Error DeleteFailed = new("Products.DeleteFailed", "Failed to delete product.", ErrorType.Internal);

        // Factory methods for dynamic messages
        public static Error NotFoundWithId(Guid productId) =>
            new("Products.NotFound", $"Product with ID {productId} not found.", ErrorType.NotFound);
    }

    public static class Users
    {
        public static readonly Error NotFound = new("Users.NotFound", "User not found.", ErrorType.NotFound);
        public static readonly Error MissingUserId = new("Users.MissingUserId", "User ID is required.", ErrorType.Validation);
        public static readonly Error UpdateFailed = new("Users.UpdateFailed", "Failed to update user profile.", ErrorType.Internal);
        public static readonly Error FetchFailed = new("Users.FetchFailed", "Failed to fetch user profile.", ErrorType.Internal);
    }

    public static class Checkout
    {
        public static readonly Error SessionNotFound = new("Checkout.SessionNotFound", "Checkout session not found.", ErrorType.NotFound);
        public static readonly Error FetchFailed = new("Checkout.FetchFailed", "Failed to fetch checkout session.", ErrorType.Internal);
        public static readonly Error CreationFailed = new("Checkout.CreationFailed", "Failed to create checkout session.", ErrorType.Internal);
    }

    public static class Subscription
    {
        public static readonly Error InvalidUser = new("Subscription.InvalidUser", "User ID is required.", ErrorType.Validation);
        public static readonly Error InvalidPriceId = new("Subscription.InvalidPriceId", "Price ID is required.", ErrorType.Validation);
        public static readonly Error SessionCreationFailed = new("Subscription.SessionCreationFailed", "Failed to create subscription session.", ErrorType.Internal);
        public static readonly Error InvalidRequest = new("Subscription.InvalidRequest", "Invalid subscription request.", ErrorType.Validation);
        public static readonly Error StripeError = new("Subscription.StripeError", "Stripe API error.", ErrorType.Internal);
        public static readonly Error FetchFailed = new("Subscription.FetchFailed", "Failed to fetch subscription status.", ErrorType.Internal);
    }
}
