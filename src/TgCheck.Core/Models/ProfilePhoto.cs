namespace TgCheck.Core.Models;

/// <summary>Одно фото из истории аватарок профиля.</summary>
public sealed class ProfilePhoto
{
    internal ProfilePhoto(TL.Photo photo) => Photo = photo;

    internal TL.Photo Photo { get; }

    public DateTime DateUtc => Photo.date;
}
