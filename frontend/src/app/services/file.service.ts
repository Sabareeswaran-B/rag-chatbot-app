import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEventType } from '@angular/common/http';
import { Observable, map } from 'rxjs';

export interface UploadResponse {
  success: boolean;
  fileName: string;
  chunksCreated: number;
  error?: string;
}

export interface UploadedFile {
  fileName: string;
  chunkCount: number;
  uploadedAt: string;
}

export interface UploadProgress {
  progress: number;
  response?: UploadResponse;
}

@Injectable({ providedIn: 'root' })
export class FileService {
  private http = inject(HttpClient);
  private baseUrl = 'http://localhost:5000/api';

  uploadFile(file: File): Observable<UploadProgress> {
    const formData = new FormData();
    formData.append('file', file);

    return this.http.post<UploadResponse>(`${this.baseUrl}/file/upload`, formData, {
      reportProgress: true,
      observe: 'events'
    }).pipe(
      map(event => {
        if (event.type === HttpEventType.UploadProgress) {
          const progress = event.total ? Math.round(100 * event.loaded / event.total) : 0;
          return { progress };
        } else if (event.type === HttpEventType.Response) {
          return { progress: 100, response: event.body as UploadResponse };
        }
        return { progress: 0 };
      })
    );
  }

  getFiles(): Observable<UploadedFile[]> {
    return this.http.get<UploadedFile[]>(`${this.baseUrl}/file/list`);
  }

  deleteFile(fileName: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/file/${encodeURIComponent(fileName)}`);
  }

  downloadFile(fileName: string): void {
    const url = `${this.baseUrl}/file/download/${encodeURIComponent(fileName)}`;
    this.http.get(url, { responseType: 'blob' }).subscribe(blob => {
      const a = document.createElement('a');
      const objectUrl = URL.createObjectURL(blob);
      a.href = objectUrl;
      a.download = fileName.replace(/\.[^.]+$/, '') + '_extracted.txt';
      a.click();
      URL.revokeObjectURL(objectUrl);
    });
  }
}
