import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

// ---------- Phase M3: hours on jobs (`/api/maintenance/jobs/{id}/hours`, docs/maintenance/plan.md §2.5) ----------

/** Hours one employee worked on the job on one day; the attendance the day showed when booked and the override that allowed more. */
export interface MaintenanceBooking {
  id: string;
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  /** `yyyy-MM-dd`, a day of the company's calendar. */
  day: string;
  hours: number;
  overtimeHours: number;
  attendanceHours: number;
  overrideReason: string | null;
  overriddenBy: string | null;
  userName: string | null;
  atUtc: string;
}

export interface MaintenanceJobHours {
  jobId: string;
  jobStatus: string;
  totalHours: number;
  bookings: MaintenanceBooking[];
  /** The company's today on the server's clock (`yyyy-MM-dd`): where a new booking starts. */
  today: string;
}

/** Someone MS-04 allows on the job that day: their attendance (null when declared per month) and what is booked already, on every record and job. */
export interface MaintenanceLabourCandidate {
  employeeId: string;
  number: string;
  fullName: string;
  attendanceHours: number | null;
  attendanceSource: string;
  bookedHours: number;
  bookedOnJob: boolean;
  payrollLocked: boolean;
}

export interface BookMaintenanceHours {
  employeeId: string;
  day: string;
  hours: number;
  overtimeHours: number;
  overrideReason: string | null;
}

@Injectable({ providedIn: 'root' })
export class MaintenanceHoursApi {
  private readonly api = inject(ApiService);

  hours(jobId: string): Promise<MaintenanceJobHours> {
    return this.api.getJson<MaintenanceJobHours>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/hours`);
  }

  /** The people who may be booked on the job that day, with the day's attendance and what is booked already. */
  candidates(jobId: string, day: string): Promise<MaintenanceLabourCandidate[]> {
    return this.api.getJson<MaintenanceLabourCandidate[]>(
      `/api/maintenance/jobs/${encodeURIComponent(jobId)}/hours/candidates?day=${encodeURIComponent(day)}`,
    );
  }

  book(jobId: string, request: BookMaintenanceHours): Promise<MaintenanceJobHours> {
    return this.api.postJson<MaintenanceJobHours>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/hours`, request);
  }

  remove(jobId: string, bookingId: string): Promise<MaintenanceJobHours> {
    return this.api.deleteJson<MaintenanceJobHours>(
      `/api/maintenance/jobs/${encodeURIComponent(jobId)}/hours/${encodeURIComponent(bookingId)}`,
    );
  }
}
