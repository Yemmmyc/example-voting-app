var app = angular.module('catsvsdogs', []);
var socket = io.connect();

var bg1 = document.getElementById('background-stats-1');
var bg2 = document.getElementById('background-stats-2');

app.controller('statsCtrl', function($scope){
  $scope.aPercent = 50;
  $scope.bPercent = 50;
  $scope.total = 0;
  $scope.aVotes = 0;
  $scope.bVotes = 0;
  $scope.connected = false;

  var scoresListenerAttached = false;

  var updateScores = function(){
    if (scoresListenerAttached) return;
    scoresListenerAttached = true;

    socket.on('scores', function (json) {
       var data;
       try {
         data = typeof json === 'string' ? JSON.parse(json) : json;
       } catch (err) {
         console.error('Error parsing scores payload:', err);
         return;
       }
       if (!data) return;

       var a = parseInt(data.a || 0, 10);
       var b = parseInt(data.b || 0, 10);

       var percentages = getPercentages(a, b);

       if (bg1) bg1.style.width = percentages.a + "%";
       if (bg2) bg2.style.width = percentages.b + "%";

       $scope.$apply(function () {
         $scope.aPercent = percentages.a;
         $scope.bPercent = percentages.b;
         $scope.aVotes = a;
         $scope.bVotes = b;
         $scope.total = a + b;
       });
    });
  };

  var init = function(){
    document.body.style.opacity = 1;
    updateScores();
  };

  socket.on('connect', function(){
    $scope.$apply(function(){
      $scope.connected = true;
    });
    init();
  });

  socket.on('disconnect', function(){
    $scope.$apply(function(){
      $scope.connected = false;
    });
  });

  socket.on('message', function(data){
    init();
  });
});

function getPercentages(a, b) {
  var result = {};

  if (a + b > 0) {
    result.a = Math.round(a / (a + b) * 100);
    result.b = 100 - result.a;
  } else {
    result.a = result.b = 50;
  }

  return result;
}
