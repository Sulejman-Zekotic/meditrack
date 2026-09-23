# MediTrack

MediTrack is a full-stack healthcare and medication tracking web application built with ASP.NET Core, Angular and SQL Server.

The application allows administrators and healthcare workers to manage medications, monitor medication history, track analytics and manage users through a responsive web interface.

---

# Live Demo

**App:** https://meditrackmt.netlify.app

**API (Swagger):** https://meditrack-api.runasp.net/swagger

> The API runs on a free plan, so the first request after a period of inactivity can take a few seconds.

---

# Demo Accounts

## Admin Account

Username: admin  
Password: Admin123!

## User Account

Username: nurse.amar  
Password: Clinic123!

---

# Features

## Authentication & Security
- JWT authentication
- Refresh tokens
- Role-based authorization
- Password reset flow
- Login rate limiting
- Secure password hashing

## Medication Management
- Add, edit and delete medications
- Medication stock tracking
- Medication history
- Medication usage analytics
- Low stock monitoring

## User Management
- Admin dashboard
- User roles
- Password reset for users
- User activity tracking

## Analytics & Statistics
- Medication usage statistics
- Daily/weekly/monthly analytics
- Dashboard charts
- Export functionality

## UI/UX
- Responsive desktop and mobile design
- Angular frontend
- Modern dashboard layout
- Toast notifications
- Mobile optimized interface

## Deployment
- Frontend: Netlify (automatic deploy from GitHub on every push)
- Backend API: MonsterASP.NET (IIS, .NET 9)
- Database: SQL Server (MonsterASP.NET), schema created by EF Core migrations on startup
- Email: Brevo SMTP relay (password setup and reset links)
- Previously deployed on Azure App Service and Azure Static Web Apps

---

# Tech Stack

## Backend
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server
- JWT Authentication
- Clean Architecture

## Frontend
- Angular
- TypeScript
- SCSS
- Angular Material

## Cloud & Tools
- Netlify
- MonsterASP.NET
- Brevo SMTP
- Swagger
- VS Code

---

# Project Structure

```txt
Backend/
 ├── ClinicApp.API
 ├── ClinicApp.Application
 ├── ClinicApp.Domain
 └── ClinicApp.Infrastructure

Frontend/
 └── ClinicAppWeb
```

---

# Running Locally

## Backend

```bash
cd Backend
dotnet restore
dotnet run --project ClinicApp.API
```

## Frontend

```bash
cd Frontend/ClinicAppWeb
npm install
ng serve
```

---
---

# Screenshots

## Login

![Login Desktop](screenshots/login-desktop.PNG)

---

## Dashboard

![Dashboard Desktop](screenshots/dashboard-desktop.PNG)

---

## Medications

![Medications Desktop](screenshots/medications-desktop.PNG)

---

## Analytics

![Analytics Desktop](screenshots/analytics-desktop.PNG)

---

## Mobile View

<table>
  <tr>
    <td align="center">
      <img src="screenshots/dashboard-mobile.png" width="220" alt="Dashboard Mobile" />
    </td>
    <td align="center">
      <img src="screenshots/medications-mobile.png" width="220" alt="Medications Mobile" />
    </td>
  </tr>
</table>

## Authors

- Sulejman Zekotic
- Amina Ahmić

## License

This project is available for portfolio and educational purposes.

# Status

Project is actively being improved and expanded.
