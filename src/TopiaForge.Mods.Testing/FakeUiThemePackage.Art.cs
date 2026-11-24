using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiThemePackage
    {
        internal OperationResult<FakeUiImages> PrepareImages(FakeUiThemeScope scope, UiNode? content, bool received)
        {
            lock (gate)
            {
                if (scope.Stopped) return Failure<FakeUiImages>(Cancelled.ErrorCode, Cancelled.ErrorMessage);
                Dictionary<UiImage, FakeUiImage>? images = null;
                List<FakeUiThemeRegistration>? packs = null;
                OperationResult<FakeUiImage>? failure = null;
                try
                {
                    UiComposition.Validate(content);
                    var presentation = UiImageProjection.Rewrite(content, Project);
                    return failure != null
                        ? Failure<FakeUiImages>(failure.ErrorCode, failure.ErrorMessage)
                        : OperationResult<FakeUiImages>.Success(new FakeUiImages(presentation, images, packs));
                }
                catch (ArgumentException error)
                {
                    return Failure<FakeUiImages>(ModErrorCode.InvalidArgument, error.Message);
                }

                UiImage? Project(UiImage image)
                {
                    images ??= new Dictionary<UiImage, FakeUiImage>();
                    if (images.TryGetValue(image, out var existing)) return existing.Presentation;
                    var projected = PrepareImage(image, received);
                    if (!projected.TryGetValue(out var value))
                    {
                        failure = projected;
                        return null;
                    }

                    images.Add(image, value);
                    if (value.Pack is FakeUiThemeRegistration pack)
                    {
                        packs ??= new List<FakeUiThemeRegistration>();
                        if (!packs.Contains(pack)) packs.Add(pack);
                    }
                    return value.Presentation;
                }
            }
        }

        private OperationResult<FakeUiImage> PrepareImage(UiImage image, bool received)
        {
            if (received && (!image.Width.HasValue || !image.Height.HasValue || image.PackDescriptorDigest.Length != 32))
                return Failure<FakeUiImage>(ModErrorCode.InvalidArgument, "$.images: A received image needs its resolved box and digest.");

            if (!registrations.TryGetValue(image.Image.PackId, out var registration))
                return received ? Fallback(image) : ImageNotDeclared();
            var verified = VerifyDefinition(registration);
            if (!verified.TryGetValue(out var descriptor))
                return received ? Fallback(image) : Failure<FakeUiImage>(verified.ErrorCode, verified.ErrorMessage);
            if (received && !image.PackDescriptorDigest.Span.SequenceEqual(descriptor.Digest.Span)) return Fallback(image);

            var metadata = UiImageProjection.Find(descriptor, image.Image.ImageId);
            if (metadata == null)
                return received ? Failure<FakeUiImage>(ModErrorCode.InvalidArgument, "$.images: The admitted descriptor does not name this image.")
                    : ImageNotDeclared();

            var frame = UiImageProjection.ResolveFrame(image, metadata);
            var presentation = received ? image : image.WithWireMetadata(frame.Width, frame.Height, descriptor.Digest.Span);
            return OperationResult<FakeUiImage>.Success(new FakeUiImage(presentation, frame, registration));
        }

        private static OperationResult<FakeUiImage> Fallback(UiImage image) =>
            OperationResult<FakeUiImage>.Success(new FakeUiImage(image, null, null));

        private static OperationResult<FakeUiImage> ImageNotDeclared() =>
            Failure<FakeUiImage>(ModErrorCode.NotFound, "$.images: This package does not declare the requested UI image.");

        private static OperationResult<T> Failure<T>(ModErrorCode code, string message) where T : notnull =>
            OperationResult<T>.Failure(code, message);
    }

    internal sealed class FakeUiImages
    {
        private readonly Dictionary<UiImage, FakeUiImage>? items;
        private readonly List<FakeUiThemeRegistration>? packs;

        internal FakeUiImages(UiNode? presentation, Dictionary<UiImage, FakeUiImage>? items, List<FakeUiThemeRegistration>? packs)
        {
            Presentation = presentation;
            this.items = items;
            this.packs = packs;
        }

        internal UiNode? Presentation { get; }
        internal IReadOnlyList<FakeUiThemeRegistration> Packs => packs ?? (IReadOnlyList<FakeUiThemeRegistration>)Array.Empty<FakeUiThemeRegistration>();
        internal FakeUiImage this[UiImage image] => items![image];
        internal bool Uses(FakeUiThemeRegistration registration) => packs?.Contains(registration) == true;
        internal bool HasImages => items != null;

        internal bool HasUnreadyLabels()
        {
            if (items == null) return false;
            foreach (var entry in items)
                if (!entry.Key.Decorative && !entry.Value.IsReady) return true;
            return false;
        }
    }

    internal sealed class FakeUiImage
    {
        internal FakeUiImage(UiImage presentation, UiImageFrame? frame, FakeUiThemeRegistration? pack)
        {
            Presentation = presentation;
            Frame = frame;
            Pack = pack;
        }

        internal UiImage Presentation { get; }
        internal UiImageFrame? Frame { get; }
        internal FakeUiThemeRegistration? Pack { get; }
        internal float Width => Presentation.Width!.Value;
        internal float Height => Presentation.Height!.Value;
        internal bool IsReady => Pack?.Loaded != null;
    }
}
