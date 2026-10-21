const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const readline = require('readline');

// Interval waktu reconnect otomatis (3 detik)
const RECONNECT_INTERVAL_MS = 3000;
const CONFIG_FILE_NAME = 'laststate.conf';
const CONFIG_PATH = path.resolve(__dirname, CONFIG_FILE_NAME);

let isShuttingDown = false;
let currentBridgeProcess = null;
let reconnectTimer = null;
let retryStartTimer = null;
let isCurrentlyConnected = false;

/**
 * Membaca ID perangkat terakhir dari file laststate.conf
 */
function readLastStateConfig(filePath) {
    if (!fs.existsSync(filePath)) {
        return null;
    }

    try {
        const content = fs.readFileSync(filePath, 'utf8');
        const lines = content.split(/\r?\n/);
        for (let i = 0; i < lines.length; i++) {
            let line = lines[i].trim();
            if (line.length === 0) {
                continue;
            }

            // Lewati baris komentar
            if (line.startsWith('#') || line.startsWith(';') || line.startsWith('//')) {
                continue;
            }

            // Periksa format key-value jika ada
            if (line.toLowerCase().startsWith('deviceid=')) {
                return line.substring('deviceid='.length).trim();
            }

            if (line.toLowerCase().startsWith('devicepath=')) {
                return line.substring('devicepath='.length).trim();
            }

            if (line.toLowerCase().startsWith('id=')) {
                return line.substring('id='.length).trim();
            }

            // Format baris teks ID langsung
            return line;
        }
    } catch (err) {
        console.error('[ERROR] Gagal membaca file konfigurasi:', err.message);
    }

    return null;
}

/**
 * Mencari lokasi file executable RawInputBridge
 */
function findRawInputBridge() {
    const candidatePaths = [
        path.resolve(__dirname, 'bin', 'RawInputBridge.exe'),
        path.resolve(__dirname, 'RawInputBridge.exe'),
        path.resolve(__dirname, '..', 'node-red-usb-rfid', 'bin', 'win', 'RawInputBridge.exe')
    ];

    for (let i = 0; i < candidatePaths.length; i++) {
        if (fs.existsSync(candidatePaths[i])) {
            return candidatePaths[i];
        }
    }

    return null;
}

/**
 * Memulai atau menghubungkan kembali bridge penyadap USB RFID
 */
function startBridge() {
    if (isShuttingDown) {
        return;
    }

    // Bersihkan timer retry jika ada
    if (retryStartTimer !== null) {
        clearTimeout(retryStartTimer);
        retryStartTimer = null;
    }

    // 1. Baca konfigurasi ID perangkat dari laststate.conf
    const targetDevicePath = readLastStateConfig(CONFIG_PATH);
    if (!targetDevicePath) {
        console.error(`[ERROR] File "${CONFIG_FILE_NAME}" tidak ditemukan atau belum ada ID.`);
        console.log(`[INFO] Reconnecting dalam ${RECONNECT_INTERVAL_MS / 1000} detik...`);
        retryStartTimer = setTimeout(startBridge, RECONNECT_INTERVAL_MS);
        return;
    }

    // 2. Cari file binary bridge Raw Input
    const bridgeExe = findRawInputBridge();
    if (!bridgeExe) {
        console.error('[ERROR] RawInputBridge.exe tidak ditemukan.');
        console.log(`[INFO] Reconnecting dalam ${RECONNECT_INTERVAL_MS / 1000} detik...`);
        retryStartTimer = setTimeout(startBridge, RECONNECT_INTERVAL_MS);
        return;
    }

    const bridgeArgs = [
        '--listen',
        '--path=' + targetDevicePath,
        '--parent-pid=' + process.pid
    ];

    let bridgeProcess = null;
    try {
        bridgeProcess = spawn(bridgeExe, bridgeArgs, {
            stdio: ['pipe', 'pipe', 'pipe']
        });
    } catch (spawnErr) {
        console.error('[ERROR] Gagal menjalankan bridge:', spawnErr.message);
        retryStartTimer = setTimeout(startBridge, RECONNECT_INTERVAL_MS);
        return;
    }

    currentBridgeProcess = bridgeProcess;

    const rl = readline.createInterface({
        input: bridgeProcess.stdout,
        terminal: false
    });

    rl.on('line', function (line) {
        let cleanLine = line.trim();
        if (cleanLine.length === 0) {
            return;
        }

        try {
            const payload = JSON.parse(cleanLine);

            if (payload.type === 'scan') {
                if (payload.uid) {
                    // Cetak nomor UID hasil scan kartu RFID ke konsol
                    console.log(payload.uid);
                }
            } else if (payload.type === 'status') {
                if (payload.state === 'connected') {
                    isCurrentlyConnected = true;

                    // Hentikan timer pesan reconnect jika sebelumnya sedang menunggu
                    if (reconnectTimer !== null) {
                        clearInterval(reconnectTimer);
                        reconnectTimer = null;
                    }

                    console.log('Berhasil terhubung ke perangkat USB RFID!');
                    console.log('Silakan scan kartu RFID (menunggu data scan)...\n');
                } else if (payload.state === 'disconnected') {
                    if (isCurrentlyConnected) {
                        console.log('\n[PERINGATAN] USB RFID terputus / dicabut!');
                    }
                    isCurrentlyConnected = false;

                    // Jalankan timer pesan notifikasi reconnect setiap 3 detik
                    if (reconnectTimer === null) {
                        reconnectTimer = setInterval(function () {
                            if (!isCurrentlyConnected && !isShuttingDown) {
                                console.log('[INFO] Reconnecting ke USB RFID... (menunggu perangkat dicolokkan kembali)');
                            }
                        }, RECONNECT_INTERVAL_MS);
                    }
                }
            } else if (payload.type === 'error') {
                console.error('[ERROR]', payload.message);
            }
        } catch (parseErr) {
            // Abaikan data selain format JSON
        }
    });

    bridgeProcess.stderr.on('data', function (data) {
        // Saluran penampung stderr bridge
    });

    bridgeProcess.on('exit', function (code) {
        currentBridgeProcess = null;
        isCurrentlyConnected = false;

        if (reconnectTimer !== null) {
            clearInterval(reconnectTimer);
            reconnectTimer = null;
        }

        // Jika bridge berhenti bukan karena perintah keluar (Ctrl+C), lakukan reconnect setelah 3 detik
        if (!isShuttingDown) {
            console.log(`[INFO] Koneksi terputus. Reconnecting dalam ${RECONNECT_INTERVAL_MS / 1000} detik...`);
            retryStartTimer = setTimeout(startBridge, RECONNECT_INTERVAL_MS);
        }
    });
}

// Inisialisasi awal program
console.log(`Membaca file konfigurasi: ${CONFIG_FILE_NAME}`);
const initialDevice = readLastStateConfig(CONFIG_PATH);
if (initialDevice) {
    console.log(`Device ID dari ${CONFIG_FILE_NAME}: ${initialDevice}`);
}

startBridge();

// Penanganan sinyal keluar terminal (Ctrl+C)
process.on('SIGINT', function () {
    isShuttingDown = true;

    if (reconnectTimer !== null) {
        clearInterval(reconnectTimer);
        reconnectTimer = null;
    }

    if (retryStartTimer !== null) {
        clearTimeout(retryStartTimer);
        retryStartTimer = null;
    }

    console.log('\nMenutup koneksi penyadap USB RFID...');
    if (currentBridgeProcess !== null) {
        try {
            currentBridgeProcess.kill();
        } catch (e) {
            // Abaikan kesalahan penutupan handle bridge
        }
    }

    process.exit(0);
});
