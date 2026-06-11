import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface ChatRequest {
  query: string;
}

export interface ChatResponse {
  answer: string;
  sources: string[];
  reasoning?: string;
  success: boolean;
  error?: string;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private http = inject(HttpClient);
  private baseUrl = 'http://localhost:5000/api';

  sendMessage(query: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>(`${this.baseUrl}/chat`, { query });
  }
}
