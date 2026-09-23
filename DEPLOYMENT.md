# MediTrack - Deployment

## Frontend (Netlify)

Netlify builds the Angular app from this repository using `netlify.toml`
(base `Frontend/ClinicAppWeb`, output `dist/ClinicApp/browser`).
The API address is set in `Frontend/ClinicAppWeb/public/app-config.json` (`apiUrl`).

## Backend (MonsterASP.NET)

1. Create `Backend/ClinicApp.API/appsettings.Local.json` (ignored by git) with:
   `ConnectionStrings:DefaultConnection`, `Jwt:Key` (min. 32 chars),
   `Cors:AllowedOrigins`, `App:FrontendBaseUrl`, `SeedData`, and the `Email` SMTP section.
2. Publish outside the project folder:

   ```
   dotnet publish Backend/ClinicApp.API -c Release -o ../meditrack-publish
   ```

3. Upload the contents of the publish folder to `/wwwroot` over SFTP and restart the site.
4. Enable HTTPS (Let's Encrypt) in the hosting panel. The frontend runs on HTTPS,
   so the API must too (browsers block mixed content).

On startup the API applies EF Core migrations and, when `SeedData:Enabled` is true,
inserts demo data.

## Notes

- SQL Server is configured with `EnableRetryOnFailure`. Manual transactions must run
  inside `Database.CreateExecutionStrategy()` (see `UserService.AddUserAsync`).
