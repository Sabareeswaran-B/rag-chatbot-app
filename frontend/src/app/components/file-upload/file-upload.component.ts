import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FileService, UploadedFile, UploadProgress } from '../../services/file.service';
import { AuthService } from '../../services/auth.service';

interface FileUploadState {
  file: File;
  progress: number;
  status: 'pending' | 'uploading' | 'done' | 'error';
  message: string;
  chunksCreated?: number;
}

@Component({
  selector: 'app-file-upload',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './file-upload.component.html',
  styleUrl: './file-upload.component.scss'
})
export class FileUploadComponent implements OnInit {
  private fileService = inject(FileService);
  authService = inject(AuthService);

  isDragOver = signal(false);
  uploadStates = signal<FileUploadState[]>([]);
  uploadedFiles = signal<UploadedFile[]>([]);
  isLoadingFiles = signal(false);

  acceptedTypes = '.pdf,.txt,.md,.docx,.csv';

  ngOnInit() {
    this.loadFiles();
  }

  loadFiles() {
    this.isLoadingFiles.set(true);
    this.fileService.getFiles().subscribe({
      next: (files) => {
        this.uploadedFiles.set(files);
        this.isLoadingFiles.set(false);
      },
      error: () => this.isLoadingFiles.set(false)
    });
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const files = Array.from(event.dataTransfer?.files || []);
    this.processFiles(files);
  }

  onFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files || []);
    this.processFiles(files);
    input.value = '';
  }

  private processFiles(files: File[]) {
    const validType = files.filter(f => this.isValidType(f));
    if (validType.length === 0) return;

    const newStates: FileUploadState[] = validType.map(f => {
      const err = this.sizeError(f);
      return {
        file: f,
        progress: 0,
        status: err ? 'error' : 'pending',
        message: err ?? 'Queued...'
      };
    });

    this.uploadStates.update(states => [...newStates, ...states]);
    newStates.forEach((state, i) => {
      if (state.status !== 'error') this.uploadFile(state, validType[i]);
    });
  }

  private isValidType(file: File): boolean {
    const ext = '.' + file.name.split('.').pop()?.toLowerCase();
    return ['.pdf', '.txt', '.md', '.docx', '.csv'].includes(ext);
  }

  private sizeError(file: File): string | null {
    const ext = '.' + file.name.split('.').pop()?.toLowerCase();
    const maxBytes = ext === '.pdf' ? 25 * 1024 * 1024 : 100 * 1024 * 1024;
    if (file.size > maxBytes) {
      return `Exceeds ${ext === '.pdf' ? '25 MB' : '100 MB'} limit`;
    }
    return null;
  }

  private uploadFile(state: FileUploadState, file: File) {
    this.uploadStates.update(states =>
      states.map(s => s.file === file ? { ...s, status: 'uploading', message: 'Uploading...' } : s)
    );

    this.fileService.uploadFile(file).subscribe({
      next: (progress: UploadProgress) => {
        this.uploadStates.update(states =>
          states.map(s => s.file === file
            ? { ...s,
                progress: progress.progress,
                status: progress.response ? (progress.response.success ? 'done' : 'error') : 'uploading',
                message: progress.response
                  ? (progress.response.success
                      ? (progress.response.isDuplicate
                          ? `Already indexed${progress.response.existingFileName && progress.response.existingFileName !== progress.response.fileName ? ' as "' + progress.response.existingFileName + '"' : ''}`
                          : `Done! ${progress.response.chunksCreated} chunks indexed`)
                      : (progress.response.error || 'Failed'))
                  : `Uploading... ${progress.progress}%`,
                chunksCreated: progress.response?.chunksCreated }
            : s)
        );
        if (progress.response?.success) this.loadFiles();
      },
      error: () => {
        this.uploadStates.update(states =>
          states.map(s => s.file === file
            ? { ...s, status: 'error', message: 'Upload failed. Check backend connection.' }
            : s)
        );
      }
    });
  }

  deleteFile(fileName: string) {
    this.fileService.deleteFile(fileName).subscribe({
      next: () => this.loadFiles(),
      error: () => {}
    });
  }

  downloadFile(fileName: string) {
    this.fileService.downloadFile(fileName);
  }

  clearCompleted() {
    this.uploadStates.update(states => states.filter(s => s.status !== 'done' && s.status !== 'error'));
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  formatHash(hash: string): string {
    return hash ? hash.slice(0, 8) + '…' : '';
  }
}
